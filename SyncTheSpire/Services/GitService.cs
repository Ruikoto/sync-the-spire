using System.Diagnostics;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace SyncTheSpire.Services;

public class GitService
{
    private readonly ConfigService _config;

    // stuff we never want committed -- goes into info/exclude instead of .gitignore
    private const string DefaultExcludeRules =
        """
        # OS junk
        Thumbs.db
        desktop.ini
        .DS_Store
        __pycache__/
        *.pyc

        # IDE / editor
        .vs/
        .idea/
        *.suo
        *.user
        """;

    public GitService(ConfigService config)
    {
        _config = config;
    }

    private string RepoPath => _config.RepoPath;
    private string GitDirPath => _config.GitDirPath;

    private bool IsSshMode => _config.LoadConfig().AuthType == "ssh";

    // HTTPS-only cred handler (SSH uses git.exe CLI instead)
    private CredentialsHandler MakeCredHandler()
    {
        var cfg = _config.LoadConfig();
        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = cfg.Username,
            Password = cfg.Token
        };
    }

    /// <summary>
    /// run git.exe with proper env vars. LibGit2Sharp 0.30 dropped SSH support,
    /// so we shell out for all network operations when SSH auth is configured.
    /// </summary>
    private void RunGitCli(string args, string? workDir = null, int timeout = 120_000)
    {
        var cfg = _config.LoadConfig();
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workDir ?? RepoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!string.IsNullOrWhiteSpace(cfg.SshKeyPath))
        {
            // ssh wants forward slashes in key path
            var keyPath = cfg.SshKeyPath.Replace("\\", "/");
            psi.Environment["GIT_SSH_COMMAND"] = $"ssh -i \"{keyPath}\" -o StrictHostKeyChecking=accept-new";
        }

        // point git to our separated git dir
        if (Directory.Exists(GitDirPath))
        {
            psi.Environment["GIT_DIR"] = GitDirPath;
            psi.Environment["GIT_WORK_TREE"] = RepoPath;
        }

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(timeout);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {args.Split(' ')[0]} failed: {stderr.Trim()}");
    }

    /// <summary>
    /// open repo via the separated git dir
    /// </summary>
    private Repository OpenRepo() => new(GitDirPath);

    // ── clone ────────────────────────────────────────────────────────────

    public void CloneRepo()
    {
        var cfg = _config.LoadConfig();

        if (IsSshMode)
        {
            // SSH: use git.exe since LibGit2Sharp 0.30 has no SSH cred classes
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"clone \"{cfg.RepoUrl}\" \"{RepoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!string.IsNullOrWhiteSpace(cfg.SshKeyPath))
            {
                var keyPath = cfg.SshKeyPath.Replace("\\", "/");
                psi.Environment["GIT_SSH_COMMAND"] = $"ssh -i \"{keyPath}\" -o StrictHostKeyChecking=accept-new";
            }

            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(300_000);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"git clone failed: {stderr.Trim()}");
        }
        else
        {
            var opts = new CloneOptions();
            opts.FetchOptions.CredentialsProvider = MakeCredHandler();
            Repository.Clone(cfg.RepoUrl, RepoPath, opts);
        }

        // separate .git dir from working tree so junction stays clean
        SeparateGitDir();

        // use info/exclude instead of .gitignore (keeps working tree pristine)
        EnsureExcludeRules();

        // remove .gitignore from working tree if the remote repo had one
        // (it stays tracked in git, just not physically in our working tree junction)
        CleanGitArtifactsFromWorkTree();
    }

    // ── branch queries ───────────────────────────────────────────────────

    public string GetCurrentBranch()
    {
        using var repo = OpenRepo();
        return repo.Head.FriendlyName;
    }

    public record BranchInfo(string Name, string Author, DateTimeOffset LastModified);

    public List<BranchInfo> GetRemoteBranches()
    {
        using var repo = OpenRepo();

        // fetch first so we have the latest refs
        FetchAll(repo);

        var remote = repo.Network.Remotes["origin"];
        if (remote is null) return [];

        return repo.Branches
            .Where(b => b.IsRemote &&
                        b.FriendlyName.StartsWith("origin/") &&
                        !b.FriendlyName.EndsWith("/HEAD"))
            .Select(b => new BranchInfo(
                b.FriendlyName.Replace("origin/", ""),
                b.Tip.Author.Name,
                b.Tip.Author.When))
            .OrderByDescending(b => b.LastModified)
            .ToList();
    }

    public bool HasLocalChanges()
    {
        using var repo = OpenRepo();
        var status = repo.RetrieveStatus(new StatusOptions());
        return status.IsDirty;
    }

    // ── sync (mode 2 - force checkout remote branch) ────────────────────

    public void ForceCheckoutBranch(string branchName)
    {
        using var repo = OpenRepo();

        FetchAll(repo);

        var remoteBranch = repo.Branches[$"origin/{branchName}"];
        if (remoteBranch is null)
            throw new InvalidOperationException($"Remote branch not found: origin/{branchName}");

        // get or create local tracking branch
        var localBranch = repo.Branches[branchName];
        if (localBranch is null)
        {
            localBranch = repo.CreateBranch(branchName, remoteBranch.Tip);
            repo.Branches.Update(localBranch,
                b => b.TrackedBranch = remoteBranch.CanonicalName);
        }

        // force checkout
        Commands.Checkout(repo, localBranch, new CheckoutOptions
        {
            CheckoutModifiers = CheckoutModifiers.Force
        });

        // reset --hard to remote tip
        repo.Reset(ResetMode.Hard, remoteBranch.Tip);

        // clean untracked files (git clean -fd)
        CleanUntracked(repo);
    }

    // ── my branch (mode 3) ───────────────────────────────────────────────

    public void CreateBranch(string branchName)
    {
        using var repo = OpenRepo();

        // if branch already exists locally, just check it out
        var existing = repo.Branches[branchName];
        if (existing is not null)
        {
            Commands.Checkout(repo, existing);
            return;
        }

        var newBranch = repo.CreateBranch(branchName);
        Commands.Checkout(repo, newBranch);

        // push so remote knows about it
        var remote = repo.Network.Remotes["origin"];
        repo.Branches.Update(newBranch,
            b => b.Remote = remote.Name,
            b => b.UpstreamBranch = newBranch.CanonicalName);

        PushCurrentBranch(repo);
    }

    public void CommitAndPush()
    {
        using var repo = OpenRepo();

        // stage everything
        Commands.Stage(repo, "*");

        var status = repo.RetrieveStatus(new StatusOptions());
        if (!status.IsDirty)
            return; // nothing to commit

        var cfg = _config.LoadConfig();
        var sig = MakeSignature(cfg);
        repo.Commit("Auto-save", sig, sig);

        PushCurrentBranch(repo);
    }

    /// <summary>
    /// silently commit any uncommitted work before switching away
    /// </summary>
    public void SilentCommitIfDirty()
    {
        using var repo = OpenRepo();
        Commands.Stage(repo, "*");

        var status = repo.RetrieveStatus(new StatusOptions());
        if (!status.IsDirty) return;

        var cfg = _config.LoadConfig();
        var sig = MakeSignature(cfg);
        repo.Commit("Auto-save (before switch)", sig, sig);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static Signature MakeSignature(Models.AppConfig cfg)
    {
        // for SSH mode, username might be empty -- fall back to "player"
        var name = string.IsNullOrWhiteSpace(cfg.Username) ? "player" : cfg.Username;
        return new Signature(name, $"{name}@sync-the-spire", DateTimeOffset.Now);
    }

    /// <summary>
    /// fetch from origin, using git.exe CLI for SSH or LibGit2Sharp for HTTPS
    /// </summary>
    private void FetchAll(Repository repo)
    {
        if (IsSshMode)
        {
            RunGitCli("fetch --all --prune");
            return;
        }

        var remote = repo.Network.Remotes["origin"];
        if (remote is null) return;

        var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
        Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
        {
            CredentialsProvider = MakeCredHandler()
        }, null);
    }

    /// <summary>
    /// push current branch to origin, using git.exe CLI for SSH or LibGit2Sharp for HTTPS
    /// </summary>
    private void PushCurrentBranch(Repository repo)
    {
        if (IsSshMode)
        {
            RunGitCli("push -u origin HEAD");
            return;
        }

        var currentBranch = repo.Head;
        repo.Network.Push(currentBranch, new PushOptions
        {
            CredentialsProvider = MakeCredHandler()
        });
    }

    /// <summary>
    /// move .git directory out of the working tree and configure core.worktree
    /// so that the junction-visible folder has zero git artifacts
    /// </summary>
    private void SeparateGitDir()
    {
        var embeddedGitDir = Path.Combine(RepoPath, ".git");
        if (!Directory.Exists(embeddedGitDir)) return;
        if (Directory.Exists(GitDirPath))
            Directory.Delete(GitDirPath, true);

        Directory.Move(embeddedGitDir, GitDirPath);

        // tell git where the working tree lives
        using var repo = new Repository(GitDirPath);
        repo.Config.Set("core.worktree", RepoPath);
    }

    /// <summary>
    /// write exclude rules into git's info/exclude (same effect as .gitignore but invisible)
    /// </summary>
    private void EnsureExcludeRules()
    {
        var infoDir = Path.Combine(GitDirPath, "info");
        Directory.CreateDirectory(infoDir);
        var excludePath = Path.Combine(infoDir, "exclude");
        File.WriteAllText(excludePath, DefaultExcludeRules);
    }

    /// <summary>
    /// remove .gitignore / .gitattributes etc from working tree if they came from the remote
    /// </summary>
    private void CleanGitArtifactsFromWorkTree()
    {
        string[] artifacts = [".gitignore", ".gitattributes"];
        foreach (var name in artifacts)
        {
            var path = Path.Combine(RepoPath, name);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void CleanUntracked(Repository repo)
    {
        var status = repo.RetrieveStatus(new StatusOptions());
        foreach (var entry in status.Untracked)
        {
            var fullPath = Path.Combine(repo.Info.WorkingDirectory, entry.FilePath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }

        // remove empty directories left behind
        RemoveEmptyDirs(repo.Info.WorkingDirectory);
    }

    private static void RemoveEmptyDirs(string root)
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            RemoveEmptyDirs(dir);
            if (Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
    }
}
