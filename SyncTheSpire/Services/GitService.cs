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
    private bool IsAnonymousMode => _config.LoadConfig().AuthType == "anonymous";

    // sentinel branch name used before the user picks a real branch
    public const string InitBranch = "_init";

    // branches that should never be checked out or pushed to by users
    private static readonly HashSet<string> ProtectedBranches = new(StringComparer.OrdinalIgnoreCase)
        { "main", "master" };

    public bool IsOnInitBranch => GetCurrentBranch() == InitBranch;

    // HTTPS cred handler — null for anonymous (public repos)
    private CredentialsHandler? MakeCredHandler()
    {
        if (IsAnonymousMode) return null;
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

        // bypass ownership check for AppData repo path (same as GlobalSettings.SetOwnerValidation)
        psi.Environment["GIT_CONFIG_COUNT"] = "1";
        psi.Environment["GIT_CONFIG_KEY_0"] = "safe.directory";
        psi.Environment["GIT_CONFIG_VALUE_0"] = "*";

        using var proc = Process.Start(psi)!;
        // read stderr before WaitForExit to avoid deadlock when pipe buffer fills
        var stderr = proc.StandardError.ReadToEnd();
        var stdout = proc.StandardOutput.ReadToEnd();
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

            // bypass ownership check for AppData repo path
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "safe.directory";
            psi.Environment["GIT_CONFIG_VALUE_0"] = "*";

            using var proc = Process.Start(psi)!;
            // read both streams before WaitForExit to avoid deadlock
            var stderr = proc.StandardError.ReadToEnd();
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(300_000);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"git clone failed: {stderr.Trim()}");
        }
        else
        {
            var opts = new CloneOptions();
            var creds = MakeCredHandler();
            if (creds != null)
                opts.FetchOptions.CredentialsProvider = creds;
            Repository.Clone(cfg.RepoUrl, RepoPath, opts);
        }

        // separate .git dir from working tree so junction stays clean
        SeparateGitDir();

        // use info/exclude instead of .gitignore (keeps working tree pristine)
        EnsureExcludeRules();

        // remove .gitignore from working tree if the remote repo had one
        // (it stays tracked in git, just not physically in our working tree junction)
        CleanGitArtifactsFromWorkTree();

        // start on an empty orphan branch so the user's mod folder isn't wiped on first connect
        CheckoutEmptyInitBranch();
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
            .Where(b => !IsProtectedBranch(b.Name) && b.Name != InitBranch)
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
        if (IsProtectedBranch(branchName))
            throw new InvalidOperationException($"不允许检出受保护的分支：{branchName}");

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
        if (IsProtectedBranch(branchName))
            throw new InvalidOperationException($"不允许创建受保护的分支名：{branchName}");

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

        if (IsProtectedBranch(repo.Head.FriendlyName))
            throw new InvalidOperationException($"不允许推送到受保护的分支：{repo.Head.FriendlyName}");

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

    public static bool IsProtectedBranch(string branchName) =>
        ProtectedBranches.Contains(branchName);

    /// <summary>
    /// create an orphan branch with no files so the mod folder starts empty after clone
    /// </summary>
    private void CheckoutEmptyInitBranch()
    {
        using var repo = OpenRepo();
        var initBranch = repo.Branches[InitBranch];
        if (initBranch is not null)
        {
            Commands.Checkout(repo, initBranch, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            return;
        }

        // create a proper orphan branch: empty tree -> commit -> checkout
        var emptyTree = repo.ObjectDatabase.CreateTree(new TreeDefinition());
        var sig = new Signature("sync-the-spire", "init@sync-the-spire", DateTimeOffset.Now);
        var commit = repo.ObjectDatabase.CreateCommit(sig, sig, "init (empty)", emptyTree, [], false);
        repo.Refs.Add($"refs/heads/{InitBranch}", commit.Id);

        var branch = repo.Branches[InitBranch]!;
        Commands.Checkout(repo, branch, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });

        // wipe all files from working tree so junction stays clean
        foreach (var f in Directory.GetFiles(RepoPath))
            File.Delete(f);
        foreach (var d in Directory.GetDirectories(RepoPath))
            Directory.Delete(d, true);
    }

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
        var fetchOpts = new FetchOptions();
        var creds = MakeCredHandler();
        if (creds != null)
            fetchOpts.CredentialsProvider = creds;
        Commands.Fetch(repo, remote.Name, refSpecs, fetchOpts, null);
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
        var pushOpts = new PushOptions();
        var creds = MakeCredHandler();
        if (creds != null)
            pushOpts.CredentialsProvider = creds;
        repo.Network.Push(currentBranch, pushOpts);
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
