using System.Diagnostics;
using System.Text;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace SyncTheSpire.Services;

public class GitService
{
    private readonly ConfigService _config;
    private readonly GitResolver _resolver;

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

    public GitService(ConfigService config, GitResolver resolver)
    {
        _config = config;
        _resolver = resolver;
    }

    private string RepoPath => _config.RepoPath;
    private string GitDirPath => _config.GitDirPath;

    private bool IsSshMode => _config.LoadConfig().AuthType == "ssh";

    // sentinel branch name used before the user picks a real branch
    public const string InitBranch = "_init";

    // branches that should never be checked out or pushed to by users
    private static readonly HashSet<string> ProtectedBranches = new(StringComparer.OrdinalIgnoreCase)
        { "main", "master" };

    public bool IsOnInitBranch => GetCurrentBranch() == InitBranch;

    // HTTPS cred handler — null for anonymous (public repos)
    private CredentialsHandler? MakeCredHandler()
    {
        var cfg = _config.LoadConfig();
        if (cfg.AuthType != "https") return null;
        if (string.IsNullOrWhiteSpace(cfg.Username) || string.IsNullOrWhiteSpace(cfg.Token)) return null;
        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = cfg.Username,
            Password = cfg.Token
        };
    }

    // ── git CLI plumbing ─────────────────────────────────────────────────

    /// <summary>
    /// set up env vars shared by all git.exe invocations:
    /// SSH key, HTTPS auth header, safe.directory, terminal prompt suppression.
    /// </summary>
    private void ConfigureGitEnv(ProcessStartInfo psi, bool setGitDir = true)
    {
        var cfg = _config.LoadConfig();

        if (!string.IsNullOrWhiteSpace(cfg.SshKeyPath))
        {
            // ssh wants forward slashes in key path
            var keyPath = cfg.SshKeyPath.Replace("\\", "/");
            psi.Environment["GIT_SSH_COMMAND"] = $"ssh -i \"{keyPath}\" -o StrictHostKeyChecking=accept-new";
        }

        // point git to our separated git dir
        if (setGitDir && Directory.Exists(GitDirPath))
        {
            psi.Environment["GIT_DIR"] = GitDirPath;
            psi.Environment["GIT_WORK_TREE"] = RepoPath;
        }

        var configIdx = 0;

        // bypass ownership check for AppData repo path (same as GlobalSettings.SetOwnerValidation)
        psi.Environment[$"GIT_CONFIG_KEY_{configIdx}"] = "safe.directory";
        psi.Environment[$"GIT_CONFIG_VALUE_{configIdx}"] = RepoPath;
        configIdx++;

        // HTTPS auth: inject Basic auth header so git.exe works with any platform (GitHub, Gitee, etc.)
        if (cfg.AuthType == "https" &&
            !string.IsNullOrWhiteSpace(cfg.Username) && !string.IsNullOrWhiteSpace(cfg.Token))
        {
            var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.Username}:{cfg.Token}"));
            psi.Environment[$"GIT_CONFIG_KEY_{configIdx}"] = "http.extraHeader";
            psi.Environment[$"GIT_CONFIG_VALUE_{configIdx}"] = $"Authorization: Basic {cred}";
            configIdx++;
        }

        psi.Environment["GIT_CONFIG_COUNT"] = configIdx.ToString();

        // never prompt for credentials interactively
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    /// <summary>
    /// run git.exe with proper env vars. used for SSH (always) and as fallback for HTTPS
    /// when LibGit2Sharp can't handle a platform's auth challenge.
    /// </summary>
    private string RunGitCli(string args, string? workDir = null, int timeout = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _resolver.GetGitPath(),
            WorkingDirectory = workDir ?? RepoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // use ArgumentList for safe arg passing instead of raw Arguments string
        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);

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

        return stdout;
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
            // SSH: always use git.exe (LibGit2Sharp 0.30 dropped SSH support)
            CloneViaGitCli(cfg);
        }
        else
        {
            // HTTPS / anonymous: try LibGit2Sharp first, fallback to git.exe
            // (some platforms like Gitee use auth schemes LibGit2Sharp can't handle)
            try
            {
                var opts = new CloneOptions();
                var creds = MakeCredHandler();
                if (creds != null)
                    opts.FetchOptions.CredentialsProvider = creds;
                Repository.Clone(cfg.RepoUrl, RepoPath, opts);
            }
            catch (LibGit2SharpException)
            {
                // clean up partial clone before retrying
                if (Directory.Exists(RepoPath))
                    Directory.Delete(RepoPath, true);
                CloneViaGitCli(cfg);
            }
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

    private void CloneViaGitCli(Models.AppConfig cfg)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _resolver.GetGitPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add(cfg.RepoUrl);
        psi.ArgumentList.Add(RepoPath);

        // clone doesn't have a GitDir yet, so skip GIT_DIR/GIT_WORK_TREE
        ConfigureGitEnv(psi, setGitDir: false);

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
    }

    // ── repo validation ─────────────────────────────────────────────────

    /// <summary>
    /// check if GitDir is actually a valid git repo (not just leftover dirs)
    /// </summary>
    public bool IsRepoValid => Repository.IsValid(GitDirPath);

    /// <summary>
    /// get the current origin remote URL from the git repo config
    /// </summary>
    public string? GetCurrentRemoteUrl()
    {
        if (!IsRepoValid) return null;
        using var repo = OpenRepo();
        var remote = repo.Network.Remotes["origin"];
        return remote?.Url;
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

    public enum BranchDivergence { UpToDate, LocalAhead, Diverged }

    public record SyncStatus(int Ahead, int Behind, bool HasRemoteBranch);

    /// <summary>
    /// fetch remote and return how many commits local is ahead/behind the remote tip.
    /// used by the refresh button -- involves network I/O so it's slow.
    /// </summary>
    public SyncStatus FetchAndGetSyncStatus()
    {
        using var repo = OpenRepo();
        FetchAll(repo);

        var remoteBranch = repo.Branches[$"origin/{repo.Head.FriendlyName}"];
        if (remoteBranch is null)
            return new SyncStatus(0, 0, HasRemoteBranch: false);

        var localTip = repo.Head.Tip;
        var remoteTip = remoteBranch.Tip;
        if (localTip.Sha == remoteTip.Sha)
            return new SyncStatus(0, 0, true);

        var mergeBase = repo.ObjectDatabase.FindMergeBase(localTip, remoteTip);
        int ahead = 0, behind = 0;
        if (mergeBase is not null)
        {
            ahead = repo.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = localTip,
                ExcludeReachableFrom = mergeBase
            }).Count();
            behind = repo.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = remoteTip,
                ExcludeReachableFrom = mergeBase
            }).Count();
        }
        return new SyncStatus(ahead, behind, true);
    }

    /// <summary>
    /// stage + commit + fetch + check divergence. returns true if push went through,
    /// false if branches have diverged (caller should ask user what to do).
    /// </summary>
    public bool CommitAndPush()
    {
        using var repo = OpenRepo();

        if (IsProtectedBranch(repo.Head.FriendlyName))
            throw new InvalidOperationException($"不允许推送到受保护的分支：{repo.Head.FriendlyName}");

        // stage everything
        Commands.Stage(repo, "*");

        var status = repo.RetrieveStatus(new StatusOptions());
        if (status.IsDirty)
        {
            var cfg = _config.LoadConfig();
            var sig = MakeSignature(cfg, ReadGitGlobalConfig("user.email"));
            repo.Commit("Auto-save", sig, sig);
        }

        // fetch first so we can detect divergence before pushing
        FetchAll(repo);

        var divergence = CheckDivergence(repo);
        if (divergence == BranchDivergence.Diverged)
            return false;

        // local is ahead or up-to-date — safe to push
        PushCurrentBranch(repo);
        return true;
    }

    /// <summary>
    /// force push local state to remote, overwriting whatever the remote has
    /// </summary>
    public void ForcePush()
    {
        using var repo = OpenRepo();

        if (IsProtectedBranch(repo.Head.FriendlyName))
            throw new InvalidOperationException($"不允许推送到受保护的分支：{repo.Head.FriendlyName}");

        ForcePushCurrentBranch(repo);
    }

    /// <summary>
    /// discard local state and reset to whatever the remote has
    /// </summary>
    public void ResetToRemote()
    {
        using var repo = OpenRepo();

        var branchName = repo.Head.FriendlyName;
        FetchAll(repo);

        var remoteBranch = repo.Branches[$"origin/{branchName}"];
        if (remoteBranch is null)
            throw new InvalidOperationException($"远端分支 {branchName} 不存在");

        repo.Reset(ResetMode.Hard, remoteBranch.Tip);
        CleanUntracked(repo);
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
        var sig = MakeSignature(cfg, ReadGitGlobalConfig("user.email"));
        repo.Commit("Auto-save (before switch)", sig, sig);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// compare local HEAD with origin tracking branch to see if they've diverged
    /// </summary>
    private static BranchDivergence CheckDivergence(Repository repo)
    {
        var branchName = repo.Head.FriendlyName;
        var remoteBranch = repo.Branches[$"origin/{branchName}"];

        // no remote branch yet — local is ahead (brand new branch)
        if (remoteBranch is null)
            return BranchDivergence.LocalAhead;

        var localTip = repo.Head.Tip;
        var remoteTip = remoteBranch.Tip;

        if (localTip.Sha == remoteTip.Sha)
            return BranchDivergence.UpToDate;

        // is remote tip an ancestor of local? -> local is simply ahead
        var mergeBase = repo.ObjectDatabase.FindMergeBase(localTip, remoteTip);
        if (mergeBase?.Sha == remoteTip.Sha)
            return BranchDivergence.LocalAhead;

        return BranchDivergence.Diverged;
    }

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

    private static Signature MakeSignature(Models.AppConfig cfg, string? gitEmail)
    {
        // for SSH/anonymous mode, nickname is the canonical identity
        var name = string.IsNullOrWhiteSpace(cfg.Nickname) ? "player" : cfg.Nickname;
        var email = gitEmail ?? $"{name}@sync-the-spire";
        return new Signature(name, email, DateTimeOffset.Now);
    }

    /// <summary>
    /// read a value from git global config, returns null if not found or git unavailable
    /// </summary>
    public string? ReadGitGlobalConfig(string key)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _resolver.GetGitPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("config");
            psi.ArgumentList.Add("--global");
            psi.ArgumentList.Add(key);

            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// fetch from origin: SSH always uses git.exe, HTTPS/anonymous try LibGit2Sharp first
    /// with git.exe fallback for platforms that LibGit2Sharp can't handle.
    /// </summary>
    private void FetchAll(Repository repo)
    {
        if (IsSshMode)
        {
            RunGitCli("fetch --all --prune");
            return;
        }

        try
        {
            var remote = repo.Network.Remotes["origin"];
            if (remote is null) return;

            var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
            var fetchOpts = new FetchOptions();
            var creds = MakeCredHandler();
            if (creds != null)
                fetchOpts.CredentialsProvider = creds;
            Commands.Fetch(repo, remote.Name, refSpecs, fetchOpts, null);
        }
        catch (LibGit2SharpException)
        {
            // fallback to git.exe for platforms with incompatible auth (e.g. Gitee)
            RunGitCli("fetch --all --prune");
        }
    }

    /// <summary>
    /// push current branch to origin: SSH always uses git.exe, HTTPS/anonymous try LibGit2Sharp
    /// first with git.exe fallback for platforms that LibGit2Sharp can't handle.
    /// validates remote URL before push and verifies the result after push.
    /// </summary>
    private void PushCurrentBranch(Repository repo)
    {
        // sanity check: make sure the repo's remote matches user config
        var cfg = _config.LoadConfig();
        var remote = repo.Network.Remotes["origin"];
        if (remote is null)
            throw new InvalidOperationException("Git 仓库未配置 origin 远端");
        if (!string.Equals(remote.Url, cfg.RepoUrl, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"远端地址不一致，期望 \"{cfg.RepoUrl}\"，实际为 \"{remote.Url}\"。请重新初始化配置。");

        var localTip = repo.Head.Tip.Sha;
        var branchName = repo.Head.FriendlyName;

        if (IsSshMode)
        {
            RunGitCli("push -u origin HEAD");
        }
        else
        {
            try
            {
                var currentBranch = repo.Head;
                var pushOpts = new PushOptions();
                var creds = MakeCredHandler();
                if (creds != null)
                    pushOpts.CredentialsProvider = creds;

                // catch per-ref push rejection that LibGit2Sharp silently swallows
                string? pushError = null;
                pushOpts.OnPushStatusError = err =>
                {
                    pushError = $"Push rejected ({err.Reference}): {err.Message}";
                };

                repo.Network.Push(currentBranch, pushOpts);

                if (pushError != null)
                    throw new LibGit2SharpException(pushError);
            }
            catch (LibGit2SharpException)
            {
                // fallback to git.exe for platforms with incompatible auth (e.g. Gitee)
                RunGitCli("push -u origin HEAD");
            }
        }

        // final check: verify the commit actually landed on the remote
        VerifyPushResult(branchName, localTip);
    }

    /// <summary>
    /// ask the remote for the branch tip and compare it to what we just pushed
    /// </summary>
    private void VerifyPushResult(string branchName, string expectedSha)
    {
        var output = RunGitCli($"ls-remote origin refs/heads/{branchName}");
        // output format: "<sha>\trefs/heads/<branch>\n"
        var remoteSha = output.Split('\t', 2).FirstOrDefault()?.Trim();

        if (string.IsNullOrEmpty(remoteSha))
            throw new InvalidOperationException(
                $"推送验证失败：远端未找到分支 {branchName}，推送可能未成功。");

        if (!string.Equals(remoteSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"推送验证失败：远端提交 ({remoteSha[..7]}) 与本地 ({expectedSha[..7]}) 不一致，推送可能未成功。");
    }

    /// <summary>
    /// force push current branch, with remote URL validation and post-push verification
    /// </summary>
    private void ForcePushCurrentBranch(Repository repo)
    {
        var cfg = _config.LoadConfig();
        var remote = repo.Network.Remotes["origin"];
        if (remote is null)
            throw new InvalidOperationException("Git 仓库未配置 origin 远端");
        if (!string.Equals(remote.Url, cfg.RepoUrl, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"远端地址不一致，期望 \"{cfg.RepoUrl}\"，实际为 \"{remote.Url}\"。请重新初始化配置。");

        var localTip = repo.Head.Tip.Sha;
        var branchName = repo.Head.FriendlyName;

        RunGitCli("push --force-with-lease -u origin HEAD");
        VerifyPushResult(branchName, localTip);
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
