using System.Diagnostics;
using LibGit2Sharp;

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

    // sentinel branch name used before the user picks a real branch
    public const string InitBranch = "_init";

    // branches that should never be checked out or pushed to by users
    private static readonly HashSet<string> ProtectedBranches = new(StringComparer.OrdinalIgnoreCase)
        { "main", "master" };

    public bool IsOnInitBranch => GetCurrentBranch() == InitBranch;

    private bool IsLocalMode => _config.LoadConfig().IsLocalMode;

    // ── git.exe CLI layer ────────────────────────────────────────────────

    // resolve bundled MinGit first, fall back to system git
    private static string ResolveGitExe()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "mingit", "cmd", "git.exe");
        return File.Exists(bundled) ? bundled : "git";
    }

    // configure env vars shared by all git.exe invocations:
    // safe.directory bypass, SSH key, HTTPS credential URL rewriting
    private void ConfigureGitEnv(ProcessStartInfo psi, string? workDir = null)
    {
        var cfg = _config.LoadConfig();
        int idx = 0;

        // bypass ownership check for AppData repo path
        psi.Environment[$"GIT_CONFIG_KEY_{idx}"] = "safe.directory";
        psi.Environment[$"GIT_CONFIG_VALUE_{idx}"] = workDir ?? RepoPath;
        idx++;

        // HTTPS auth: rewrite URL to embed credentials (per-process only, never persisted)
        if (cfg.AuthType == "https" &&
            !string.IsNullOrWhiteSpace(cfg.Username) &&
            !string.IsNullOrWhiteSpace(cfg.Token) &&
            !string.IsNullOrWhiteSpace(cfg.RepoUrl))
        {
            var uri = new Uri(cfg.RepoUrl);
            var cleanBase = $"{uri.Scheme}://{uri.Host}/";
            var authBase = $"{uri.Scheme}://{Uri.EscapeDataString(cfg.Username)}:{Uri.EscapeDataString(cfg.Token)}@{uri.Host}/";
            psi.Environment[$"GIT_CONFIG_KEY_{idx}"] = $"url.{authBase}.insteadOf";
            psi.Environment[$"GIT_CONFIG_VALUE_{idx}"] = cleanBase;
            idx++;
        }

        psi.Environment["GIT_CONFIG_COUNT"] = idx.ToString();

        // SSH auth: point git at the private key
        if (cfg.AuthType == "ssh" && !string.IsNullOrWhiteSpace(cfg.SshKeyPath))
        {
            var keyPath = cfg.SshKeyPath.Replace("\\", "/");
            psi.Environment["GIT_SSH_COMMAND"] = $"ssh -i \"{keyPath}\" -o StrictHostKeyChecking=accept-new";
        }

        // never hang waiting for interactive credential input
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    /// <summary>
    /// run git.exe for network and misc CLI operations.
    /// all auth (SSH, HTTPS, anonymous) is handled via env vars.
    /// </summary>
    private void RunGitCli(string args, string? workDir = null, int timeout = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveGitExe(),
            WorkingDirectory = workDir ?? RepoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // use ArgumentList for safe arg passing instead of raw Arguments string
        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);

        // point git to our separated git dir
        if (Directory.Exists(GitDirPath))
        {
            psi.Environment["GIT_DIR"] = GitDirPath;
            psi.Environment["GIT_WORK_TREE"] = RepoPath;
        }

        ConfigureGitEnv(psi);

        using var proc = Process.Start(psi)!;
        // read stderr async to avoid deadlock when pipe buffer fills
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdout = proc.StandardOutput.ReadToEnd();

        if (!proc.WaitForExit(timeout))
        {
            proc.Kill();
            throw new InvalidOperationException($"git {psi.ArgumentList.FirstOrDefault()} timed out after {timeout / 1000}s");
        }

        var stderr = stderrTask.Result;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {psi.ArgumentList.FirstOrDefault()} failed: {stderr.Trim()}");
    }

    /// <summary>
    /// open repo via the separated git dir
    /// </summary>
    private Repository OpenRepo() => new(GitDirPath);

    // ── clone ────────────────────────────────────────────────────────────

    public void CloneRepo()
    {
        var cfg = _config.LoadConfig();

        // all auth modes use git.exe -- unified network layer
        var psi = new ProcessStartInfo
        {
            FileName = ResolveGitExe(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add(cfg.RepoUrl);
        psi.ArgumentList.Add(RepoPath);

        ConfigureGitEnv(psi);

        using var proc = Process.Start(psi)!;
        // read stderr async to avoid deadlock when pipe buffer fills
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdout = proc.StandardOutput.ReadToEnd();

        if (!proc.WaitForExit(300_000))
        {
            proc.Kill();
            throw new InvalidOperationException("git clone timed out after 300s");
        }

        var stderr = stderrTask.Result;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git clone failed: {stderr.Trim()}");

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

    /// <summary>
    /// initialize a fresh local git repo (no remote) with separate git dir
    /// </summary>
    public void InitLocalRepo()
    {
        Directory.CreateDirectory(RepoPath);
        Repository.Init(RepoPath);
        SeparateGitDir();
        EnsureExcludeRules();
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

    public List<BranchInfo> GetLocalBranches()
    {
        using var repo = OpenRepo();
        return repo.Branches
            .Where(b => !b.IsRemote)
            .Select(b => new BranchInfo(
                b.FriendlyName,
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

        // local mode: checkout local branch directly (no fetch, no remote tracking)
        if (IsLocalMode)
        {
            var branch = repo.Branches[branchName];
            if (branch is null)
                throw new InvalidOperationException($"本地分支不存在：{branchName}");

            Commands.Checkout(repo, branch, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force
            });
            repo.Reset(ResetMode.Hard, branch.Tip);
            CleanUntracked(repo);
            return;
        }

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

            // branch might have been created in local mode and now we're online —
            // ensure upstream tracking is set and push
            if (!IsLocalMode && !existing.IsTracking)
            {
                var remote = repo.Network.Remotes["origin"];
                if (remote is not null)
                {
                    repo.Branches.Update(existing,
                        b => b.Remote = remote.Name,
                        b => b.UpstreamBranch = existing.CanonicalName);
                    PushCurrentBranch(repo);
                }
            }

            return;
        }

        var newBranch = repo.CreateBranch(branchName);
        Commands.Checkout(repo, newBranch);

        if (!IsLocalMode)
        {
            // push so remote knows about it
            var remote = repo.Network.Remotes["origin"];
            repo.Branches.Update(newBranch,
                b => b.Remote = remote.Name,
                b => b.UpstreamBranch = newBranch.CanonicalName);

            PushCurrentBranch(repo);
        }
    }

    /// <returns>true if changes were committed (and pushed in remote mode), false if nothing to commit</returns>
    public bool CommitAndPush()
    {
        using var repo = OpenRepo();

        if (IsProtectedBranch(repo.Head.FriendlyName))
            throw new InvalidOperationException($"不允许推送到受保护的分支：{repo.Head.FriendlyName}");

        // stage everything
        Commands.Stage(repo, "*");

        var status = repo.RetrieveStatus(new StatusOptions());
        if (!status.IsDirty)
            return false; // nothing to commit

        var cfg = _config.LoadConfig();
        var sig = MakeSignature(cfg);
        repo.Commit("Auto-save", sig, sig);

        PushCurrentBranch(repo);
        return true;
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

    // ── remote management ─────────────────────────────────────────────

    /// <summary>
    /// get the current origin remote URL, or null if no origin exists
    /// </summary>
    public string? GetOriginUrl()
    {
        using var repo = OpenRepo();
        return repo.Network.Remotes["origin"]?.Url;
    }

    /// <summary>
    /// make sure the "origin" remote exists and points to the right URL.
    /// handles local-to-remote switch (add) or remote-to-local (remove).
    /// for URL changes between different remotes, caller should re-clone instead.
    /// </summary>
    public void EnsureRemote(string? repoUrl, bool isLocalMode)
    {
        using var repo = OpenRepo();
        var existing = repo.Network.Remotes["origin"];

        if (isLocalMode)
        {
            // local mode doesn't need a remote — remove if left over
            if (existing is not null)
                repo.Network.Remotes.Remove("origin");
            return;
        }

        if (string.IsNullOrWhiteSpace(repoUrl))
            return;

        if (existing is null)
        {
            // local-to-remote switch: add origin for the first time
            repo.Network.Remotes.Add("origin", repoUrl);
        }
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
    /// fetch from origin using git.exe CLI (all auth modes)
    /// </summary>
    private void FetchAll(Repository repo)
    {
        if (IsLocalMode) return; // no remote to fetch from
        RunGitCli("fetch --all --prune");
    }

    /// <summary>
    /// push current branch to origin using git.exe CLI (all auth modes)
    /// </summary>
    private void PushCurrentBranch(Repository repo)
    {
        if (IsLocalMode) return; // no remote to push to
        RunGitCli("push -u origin HEAD");
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
