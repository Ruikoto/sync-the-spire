using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using SyncTheSpire.Helpers;
using SyncTheSpire.Models;

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

    // progress data from git transfer operations (clone/fetch/push)
    public record GitTransferProgress(int Percent, string Detail);

    public GitService(ConfigService config, GitResolver resolver)
    {
        _config = config;
        _resolver = resolver;
    }

    /// <summary>
    /// set by handler before network ops, cleared after. fires with parsed progress from git stderr.
    /// </summary>
    public Action<GitTransferProgress>? OnTransferProgress { get; set; }

    /// <summary>
    /// surfaces LFS-related warnings to the UI (install/checkout failures, etc.).
    /// the binary itself is bundled, so there's no download progress to report.
    /// </summary>
    public Action<string>? OnLfsMessage { get; set; }

    private string RepoPath => _config.RepoPath;
    private string GitDirPath => _config.GitDirPath;
    private string WorkTreePath => _config.WorkTreePath;

    private bool IsSshMode => _config.Workspace.AuthType == "ssh";

    // sentinel branch name used before the user picks a real branch
    public const string InitBranch = "_init";

    // branches that should never be checked out or pushed to by users
    private static readonly HashSet<string> ProtectedBranches = new(StringComparer.OrdinalIgnoreCase)
        { "main", "master" };

    public bool IsOnInitBranch => GetCurrentBranch() == InitBranch;

    // HTTPS cred handler — null for anonymous (public repos)
    private CredentialsHandler? MakeCredHandler()
    {
        var ws = _config.Workspace;
        if (ws.AuthType != "https") return null;
        if (string.IsNullOrWhiteSpace(ws.Username) || string.IsNullOrWhiteSpace(ws.Token)) return null;
        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = ws.Username,
            Password = ws.Token
        };
    }

    // ── git CLI plumbing ─────────────────────────────────────────────────

    /// <summary>
    /// set up env vars shared by all git.exe invocations:
    /// PATH, SSH key, HTTPS auth header, safe.directory, terminal prompt suppression.
    /// </summary>
    private void ConfigureGitEnv(ProcessStartInfo psi, bool setGitDir = true)
    {
        var ws = _config.Workspace;

        // git.exe's compiled-in --exec-path is mingw64/libexec/git-core, but MinGit's
        // native remote helpers (git-remote-http(s).exe etc.) live in mingw64/bin instead;
        // ssh.exe used for SSH auth lives in usr/bin. without those dirs on PATH,
        // `git fetch/push https://...` dies with "remote-helper 'https' aborted" and SSH
        // fails to resolve. system-installed git masks this through cmd/git.exe wrapper or
        // its installer's PATH entries; we spawn mingw64/bin/git.exe directly with no
        // system git on the machine, so we have to prepend the dirs ourselves.
        var gitBinDir = Path.GetDirectoryName(_resolver.GetGitPath())!;
        var usrBinDir = Path.Combine(gitBinDir, "..", "..", "usr", "bin");
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.Environment["PATH"] = string.IsNullOrEmpty(existingPath)
            ? $"{gitBinDir};{usrBinDir}"
            : $"{gitBinDir};{usrBinDir};{existingPath}";

        if (!string.IsNullOrWhiteSpace(ws.SshKeyPath))
        {
            // ssh wants forward slashes in key path
            var keyPath = ws.SshKeyPath.Replace("\\", "/");
            psi.Environment["GIT_SSH_COMMAND"] = $"ssh -i \"{keyPath}\" -o StrictHostKeyChecking=accept-new";
        }

        // point git to our separated git dir
        if (setGitDir && Directory.Exists(GitDirPath))
        {
            psi.Environment["GIT_DIR"] = GitDirPath;
            psi.Environment["GIT_WORK_TREE"] = WorkTreePath;
        }

        var configIdx = 0;

        // bypass ownership check for working tree path (same as GlobalSettings.SetOwnerValidation)
        psi.Environment[$"GIT_CONFIG_KEY_{configIdx}"] = "safe.directory";
        psi.Environment[$"GIT_CONFIG_VALUE_{configIdx}"] = WorkTreePath;
        configIdx++;

        // HTTPS auth: inject Basic auth header so git.exe works with any platform (GitHub, Gitee, etc.)
        if (ws.AuthType == "https" &&
            !string.IsNullOrWhiteSpace(ws.Username) && !string.IsNullOrWhiteSpace(ws.Token))
        {
            var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ws.Username}:{ws.Token}"));
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
    /// L3 fix: parse args more carefully — split respects quoted segments
    /// </summary>
    private string RunGitCli(string args, string? workDir = null, int timeout = 120_000)
    {
        LogService.Info($"git.exe {args}");
        var psi = new ProcessStartInfo
        {
            FileName = _resolver.GetGitPath(),
            WorkingDirectory = workDir ?? WorkTreePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // split aware of double-quoted segments so paths with spaces work
        foreach (var arg in SplitArgs(args))
            psi.ArgumentList.Add(arg);

        ConfigureGitEnv(psi);

        using var proc = Process.Start(psi)!;
        // read both streams async — sync read on either stream would deadlock when pipe
        // buffer fills (e.g. ls-files / branch --format on large repos)
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();

        if (!proc.WaitForExit(timeout))
        {
            proc.Kill();
            throw new InvalidOperationException($"git {psi.ArgumentList.FirstOrDefault()} timed out after {timeout / 1000}s");
        }

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {psi.ArgumentList.FirstOrDefault()} failed: {stderr.Trim()}");

        return stdout;
    }

    // regex for git progress lines: "Receiving objects:  45% (1234/2742), 10.67 MiB | ..."
    private static readonly Regex ProgressPercentRx = new(@"(\d+)%", RegexOptions.Compiled);
    private static readonly Regex ProgressSizeRx =
        new(@"\(([0-9.]+\s*\w+)\s*/\s*([0-9.]+\s*\w+)\)", RegexOptions.Compiled);

    /// <summary>
    /// run git.exe while streaming stderr for progress updates.
    /// fires OnTransferProgress callback (throttled to 200ms) as git reports progress.
    /// </summary>
    private void RunGitCliWithProgress(string args, string? workDir = null,
        int timeout = 120_000, bool setGitDir = true)
    {
        LogService.Info($"git.exe {args} (with progress)");
        var psi = new ProcessStartInfo
        {
            FileName = _resolver.GetGitPath(),
            WorkingDirectory = workDir ?? WorkTreePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in SplitArgs(args))
            psi.ArgumentList.Add(arg);

        ConfigureGitEnv(psi, setGitDir);

        using var proc = Process.Start(psi)!;
        var stderrBuilder = new StringBuilder();
        var lastReport = Stopwatch.GetTimestamp();

        // git progress uses \r to overwrite lines, read char-by-char to catch them
        var lineBuffer = new StringBuilder();
        var stderrStream = proc.StandardError;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();

        while (!stderrStream.EndOfStream)
        {
            var ch = (char)stderrStream.Read();
            if (ch == '\r' || ch == '\n')
            {
                var line = lineBuffer.ToString();
                lineBuffer.Clear();
                stderrBuilder.AppendLine(line);

                if (OnTransferProgress != null && line.Length > 0)
                {
                    // only report the heavy transfer phase — skip Counting/Compressing/Enumerating
                    // which complete instantly and cause the progress bar to jump 0→100→back
                    var isTransferPhase = line.Contains("Receiving objects") ||
                                          line.Contains("Resolving deltas") ||
                                          line.Contains("Writing objects");

                    var pctMatch = ProgressPercentRx.Match(line);
                    if (pctMatch.Success && isTransferPhase)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(lastReport);
                        var pct = int.Parse(pctMatch.Groups[1].Value);
                        // throttle: report at most every 200ms, or on 100%
                        if (elapsed.TotalMilliseconds >= 200 || pct >= 100)
                        {
                            lastReport = Stopwatch.GetTimestamp();
                            // build detail from the full line
                            var detail = line.Trim();
                            // try to extract size info for a cleaner detail
                            var sizeMatch = ProgressSizeRx.Match(line);
                            if (sizeMatch.Success)
                                detail = $"{pct}% ({sizeMatch.Groups[1].Value} / {sizeMatch.Groups[2].Value})";

                            OnTransferProgress(new GitTransferProgress(pct, detail));
                        }
                    }
                }
            }
            else
            {
                lineBuffer.Append(ch);
            }
        }

        if (!proc.WaitForExit(timeout))
        {
            proc.Kill();
            throw new InvalidOperationException($"git {psi.ArgumentList.FirstOrDefault()} timed out after {timeout / 1000}s");
        }

        var stderr = stderrBuilder.ToString();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {psi.ArgumentList.FirstOrDefault()} failed: {stderr.Trim()}");
    }

    /// <summary>
    /// run git-lfs.exe directly with proper git dir env vars
    /// </summary>
    private string RunGitLfsCli(string args, int timeout = 120_000)
    {
        LogService.Info($"git-lfs.exe {args}");
        var psi = new ProcessStartInfo
        {
            FileName = _resolver.GetGitLfsPath(),
            WorkingDirectory = WorkTreePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in SplitArgs(args))
            psi.ArgumentList.Add(arg);
        ConfigureGitEnv(psi);

        using var proc = Process.Start(psi)!;
        // read both streams async to avoid pipe-buffer deadlock when output is large
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();

        if (!proc.WaitForExit(timeout))
        {
            proc.Kill();
            throw new InvalidOperationException($"git-lfs {psi.ArgumentList.FirstOrDefault()} timed out after {timeout / 1000}s");
        }

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git-lfs {psi.ArgumentList.FirstOrDefault()} failed: {stderr.Trim()}");
        return stdout;
    }

    /// <summary>
    /// open repo via the separated git dir
    /// </summary>
    private Repository OpenRepo() => new(GitDirPath);

    // ── clone ────────────────────────────────────────────────────────────

    public void CloneRepo()
    {
        var ws = _config.Workspace;
        LogService.Info($"Cloning repo: {ws.RepoUrl}");

        // always use git.exe for clone — LibGit2Sharp doesn't support --depth
        CloneViaGitCli();

        // separate .git dir from working tree so junction stays clean
        SeparateGitDir();
        LogService.Info("Clone completed, git dir separated");

        // use info/exclude instead of .gitignore (keeps working tree pristine)
        EnsureExcludeRules();

        // auto-detect LFS config in the cloned repo before we clean artifacts.
        // skip materialize: CheckoutEmptyInitBranch is about to wipe the working tree,
        // so any LFS content we'd download here gets thrown away. defer until the user
        // picks a real branch (ForceCheckoutBranch / ResetToRemote).
        DetectAndInstallLfsIfNeeded(materialize: false);

        // remove .gitignore from working tree if the remote repo had one
        // (it stays tracked in git, just not physically in our working tree junction)
        CleanGitArtifactsFromWorkTree();

        // start on an empty orphan branch so the user's mod folder isn't wiped on first connect
        CheckoutEmptyInitBranch();
    }

    private void CloneViaGitCli()
    {
        // shallow clone with progress — cuts initial download size dramatically for large repos
        // --no-single-branch: depth=1 implies --single-branch which breaks subsequent fetch --prune
        // (it sets remote.origin.fetch to only the default branch, so prune kills all other refs)
        var url = _config.Workspace.RepoUrl;
        RunGitCliWithProgress($"clone --depth 1 --no-single-branch --progress \"{url}\" \"{RepoPath}\"",
            workDir: Path.GetDirectoryName(RepoPath), timeout: 300_000, setGitDir: false);
    }

    // ── repo validation ─────────────────────────────────────────────────

    /// <summary>
    /// check if GitDir is actually a valid git repo (not just leftover dirs)
    /// </summary>
    public bool IsRepoValid => Repository.IsValid(GitDirPath);

    /// <summary>
    /// update core.worktree in git config — needed when WorkTreePath changes
    /// after initial clone (e.g. generic adapter: GameInstallPath set after context creation)
    /// </summary>
    public void UpdateWorkTree()
    {
        if (!IsRepoValid) return;
        using var repo = OpenRepo();
        repo.Config.Set("core.worktree", WorkTreePath);
    }

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

        // `.Tip.Author.*` will throw NotFoundException if the branch tip references an object
        // the local object DB doesn't have (shallow boundary / stale db). Skip those branches
        // rather than failing the whole listing.
        var results = new List<BranchInfo>();
        foreach (var b in repo.Branches)
        {
            if (!b.IsRemote) continue;
            if (!b.FriendlyName.StartsWith("origin/")) continue;
            if (b.FriendlyName.EndsWith("/HEAD")) continue;

            var name = b.FriendlyName.Replace("origin/", "");
            if (IsProtectedBranch(name) || name == InitBranch) continue;

            try
            {
                var tip = b.Tip;
                results.Add(new BranchInfo(name, tip.Author.Name, tip.Author.When));
            }
            catch (LibGit2SharpException ex)
            {
                LogService.Warn($"[GetRemoteBranches] skipping {b.FriendlyName}: {ex.Message}");
            }
        }
        return results.OrderByDescending(b => b.LastModified).ToList();
    }

    public bool HasLocalChanges()
    {
        try
        {
            using var repo = OpenRepo();
            var status = repo.RetrieveStatus(new StatusOptions());
            return status.IsDirty;
        }
        catch (LibGit2SharpException ex)
        {
            // object-db inconsistency (shallow boundary / stale db) can blow up RetrieveStatus
            // — degrading to "no local changes" keeps refresh alive; if the user really has
            // changes they'll find out on the next push attempt.
            LogService.Warn($"[HasLocalChanges] {ex.GetType().Name}: {ex.Message} — assuming clean");
            return false;
        }
    }

    // ── sync (mode 2 - force checkout remote branch) ────────────────────

    public void ForceCheckoutBranch(string branchName)
    {
        LogService.Info($"Force checkout branch: {branchName}");
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

        // materialize LFS pointers → real content; without this "拉取后本地=远程" is broken
        // for repos using LFS (since SYNC_OTHER_BRANCH is the primary pull path from the UI).
        DetectAndInstallLfsIfNeeded();
    }

    // ── my branch (mode 3) ───────────────────────────────────────────────

    public void CreateBranch(string branchName)
    {
        LogService.Info($"Creating branch: {branchName}");
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
        return ReadSyncStatus(repo);
    }

    /// <summary>
    /// fetch from remote without holding any gate — safe to call concurrently with
    /// other read-only operations. only touches remote-tracking refs, not the working tree.
    /// </summary>
    public void Fetch()
    {
        using var repo = OpenRepo();
        FetchAll(repo);
    }

    /// <summary>
    /// return ahead/behind counts against the remote tip WITHOUT fetching first.
    /// assumes remote refs are already up-to-date (caller fetched separately).
    /// </summary>
    public SyncStatus GetSyncStatus()
    {
        using var repo = OpenRepo();
        return ReadSyncStatus(repo);
    }

    // shared read path for both sync-status APIs. structured so the UI can tell apart three
    // states: truly in sync, definitely has remote updates with known count, and "tips differ
    // but count unknown" (shallow boundary fallout). Last case returns behind=-1 so the
    // frontend surfaces a "remote has changes" prompt instead of falsely showing "up to date".
    private static SyncStatus ReadSyncStatus(Repository repo)
    {
        // step 1 — read tips. if this fails we truly know nothing; degrade to "looks synced".
        Commit localTip, remoteTip;
        try
        {
            var remoteBranch = repo.Branches[$"origin/{repo.Head.FriendlyName}"];
            if (remoteBranch is null)
                return new SyncStatus(0, 0, HasRemoteBranch: false);
            localTip = repo.Head.Tip;
            remoteTip = remoteBranch.Tip;
        }
        catch (LibGit2SharpException ex)
        {
            LogService.Warn($"[ReadSyncStatus] cannot read tips: {ex.GetType().Name}: {ex.Message}");
            return new SyncStatus(0, 0, true);
        }

        if (localTip.Sha == remoteTip.Sha)
            return new SyncStatus(0, 0, true);

        // step 2 — tips differ, so updates definitely exist. try to count precisely; if the
        // walk hits a shallow boundary, surface "behind=-1" sentinel.
        try
        {
            var mergeBase = TryFindMergeBase(repo, localTip, remoteTip);
            if (mergeBase is null)
                return new SyncStatus(0, -1, true);

            var ahead = repo.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = localTip,
                ExcludeReachableFrom = mergeBase
            }).Count();
            var behind = repo.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = remoteTip,
                ExcludeReachableFrom = mergeBase
            }).Count();
            return new SyncStatus(ahead, behind, true);
        }
        catch (LibGit2SharpException ex)
        {
            LogService.Warn($"[ReadSyncStatus] diff detected but count failed: {ex.GetType().Name}: {ex.Message}");
            return new SyncStatus(0, -1, true);
        }
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

        var preCommitSha = repo.Head.Tip?.Sha;
        bool committedThisCall = false;

        // stage everything
        StageAll(repo);

        var status = repo.RetrieveStatus(new StatusOptions());
        if (status.IsDirty)
        {
            var ws = _config.Workspace;
            var sig = MakeSignature(ws, ReadGitGlobalConfig("user.email"));
            var msg = BuildCommitMessage(status);
            repo.Commit(msg, sig, sig);
            committedThisCall = true;
            LogService.Info($"Committed: {msg}");
        }

        // fetch first so we can detect divergence before pushing
        FetchAll(repo);

        var divergence = CheckDivergence(repo);
        if (divergence == BranchDivergence.Diverged)
        {
            LogService.Warn("Branch diverged, push aborted — awaiting user resolution");
            return false;
        }

        // local is ahead or up-to-date — safe to push
        try { PushCurrentBranch(repo); }
        catch
        {
            if (committedThisCall && preCommitSha != null)
            {
                RunGitCli($"reset --soft {preCommitSha}");
                LogService.Warn("Push failed, rolled back local commit to preserve working tree");
            }
            throw;
        }
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
        LogService.Info($"Resetting to remote: origin/{branchName}");
        FetchAll(repo);

        var remoteBranch = repo.Branches[$"origin/{branchName}"];
        if (remoteBranch is null)
            throw new InvalidOperationException($"远端分支 {branchName} 不存在");

        repo.Reset(ResetMode.Hard, remoteBranch.Tip);
        CleanUntracked(repo);
        DetectAndInstallLfsIfNeeded();
    }

    /// <summary>
    /// silently commit any uncommitted work before switching away
    /// </summary>
    public void SilentCommitIfDirty()
    {
        using var repo = OpenRepo();
        StageAll(repo);

        var status = repo.RetrieveStatus(new StatusOptions());
        if (!status.IsDirty) return;

        var ws = _config.Workspace;
        var sig = MakeSignature(ws, ReadGitGlobalConfig("user.email"));
        repo.Commit("Snapshot before branch switch", sig, sig);
        LogService.Info("Silent auto-commit before branch switch");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// build a human-readable commit message from staged changes.
    /// summarizes top-level folders (mods) that were added/updated/removed.
    /// </summary>
    private static string BuildCommitMessage(RepositoryStatus status)
    {
        var added = new HashSet<string>();
        var modified = new HashSet<string>();
        var removed = new HashSet<string>();

        foreach (var entry in status)
        {
            var s = entry.State;
            // top-level folder name (or filename if at root)
            var key = entry.FilePath.Contains('/')
                ? entry.FilePath[..entry.FilePath.IndexOf('/')]
                : entry.FilePath;

            if (s.HasFlag(FileStatus.NewInIndex) || s.HasFlag(FileStatus.NewInWorkdir))
                added.Add(key);
            else if (s.HasFlag(FileStatus.DeletedFromIndex) || s.HasFlag(FileStatus.DeletedFromWorkdir))
                removed.Add(key);
            else if (s.HasFlag(FileStatus.ModifiedInIndex) || s.HasFlag(FileStatus.ModifiedInWorkdir)
                  || s.HasFlag(FileStatus.RenamedInIndex) || s.HasFlag(FileStatus.RenamedInWorkdir)
                  || s.HasFlag(FileStatus.TypeChangeInIndex) || s.HasFlag(FileStatus.TypeChangeInWorkdir))
                modified.Add(key);
        }

        // remove overlap: if something shows up in both added and modified, keep added
        modified.ExceptWith(added);
        modified.ExceptWith(removed);

        var parts = new List<string>();
        if (added.Count > 0) parts.Add($"Add {FormatNames(added)}");
        if (modified.Count > 0) parts.Add($"Update {FormatNames(modified)}");
        if (removed.Count > 0) parts.Add($"Remove {FormatNames(removed)}");

        return parts.Count > 0 ? string.Join("; ", parts) : "Sync changes";
    }

    private static string FormatNames(HashSet<string> names)
    {
        const int maxShow = 3;
        var sorted = names.OrderBy(n => n).ToList();
        if (sorted.Count <= maxShow)
            return string.Join(", ", sorted);
        return $"{string.Join(", ", sorted.Take(maxShow))} (+{sorted.Count - maxShow} more)";
    }

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
        var mergeBase = TryFindMergeBase(repo, localTip, remoteTip);
        if (mergeBase is null)
            return BranchDivergence.Diverged;

        if (mergeBase.Sha == remoteTip.Sha)
            return BranchDivergence.LocalAhead;

        return BranchDivergence.Diverged;
    }

    // null-safe wrapper — LibGit2Sharp stale object db can throw after git.exe fallback fetches
    private static Commit? TryFindMergeBase(Repository repo, Commit a, Commit b)
    {
        try { return repo.ObjectDatabase.FindMergeBase(a, b); }
        catch (Exception) { return null; }
    }

    // host-based file size limits for pre-commit preflight
    private static readonly (string HostContains, int Mib)[] HostLimits =
    [
        ("github.com",    99),
        ("atomgit.com",   99),
        ("gitcode.com",   99),
        ("gitlab.com",    99),
        ("bitbucket.org", 99),
        ("gitee.com",     49),
    ];

    public record LargeFile(string RelativePath, long SizeBytes);

    public record OrphanResult(string Branch, bool Success, string? Error);

    /// <summary>
    /// lowercased repo host (github.com, gitee.com, etc.). handles HTTPS and SSH urls.
    /// returns empty string if the url can't be parsed.
    /// </summary>
    public static string GetRepoHost(string repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return string.Empty;

        if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            return uri.Host.ToLowerInvariant();

        if (repoUrl.StartsWith("git@"))
        {
            var at = repoUrl.IndexOf('@');
            var colon = repoUrl.IndexOf(':', at);
            var host = colon > at ? repoUrl[(at + 1)..colon] : repoUrl[(at + 1)..];
            return host.ToLowerInvariant();
        }

        return repoUrl.ToLowerInvariant();
    }

    /// <summary>
    /// resolve effective size limit from settings; auto-detects based on repo host
    /// </summary>
    public long GetEffectiveSizeLimitBytes()
    {
        var ws = _config.Workspace;
        if (ws.MaxFileSizeMode == "unlimited") return long.MaxValue;
        if (ws.MaxFileSizeMode == "manual") return (long)ws.MaxFileSizeManualMib * 1024 * 1024;

        var host = GetRepoHost(ws.RepoUrl);
        foreach (var (hostContains, mib) in HostLimits)
            if (host.Contains(hostContains, StringComparison.OrdinalIgnoreCase))
                return (long)mib * 1024 * 1024;

        return 49L * 1024 * 1024; // conservative default
    }

    /// <summary>
    /// scan working tree for files about to be pushed that exceed the size limit;
    /// only includes new/modified files (respects .gitignore). already-tracked unchanged
    /// files are skipped — their blobs aren't going over the wire on this push.
    /// </summary>
    public List<LargeFile> ScanLargeFiles(long limitBytes)
    {
        var output = RunGitCli("ls-files --others --exclude-standard --modified -z");
        var result = new List<LargeFile>();

        foreach (var rel in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fullPath = Path.Combine(WorkTreePath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;
            var size = new FileInfo(fullPath).Length;
            if (size > limitBytes)
                result.Add(new LargeFile(rel, size));
        }

        return result;
    }

    /// <summary>
    /// append exclude patterns to git's info/exclude, deduplicating existing entries
    /// </summary>
    public void AppendExcludeRules(IEnumerable<string> patterns)
    {
        var infoDir = Path.Combine(GitDirPath, "info");
        Directory.CreateDirectory(infoDir);
        var excludePath = Path.Combine(infoDir, "exclude");

        var existing = File.Exists(excludePath)
            ? new HashSet<string>(File.ReadAllLines(excludePath), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toAdd = patterns.Where(p => !existing.Contains(p)).ToList();
        if (toAdd.Count == 0) return;

        using var writer = File.AppendText(excludePath);
        foreach (var p in toAdd)
            writer.WriteLine(p);
    }

    /// <summary>
    /// install LFS hooks in the separated git dir and mark workspace as LFS-enabled.
    /// </summary>
    public void EnableLfs()
    {
        RunGitLfsCli("install --local");

        var ws = _config.Workspace;
        ws.LfsEnabled = true;
        _config.SaveWorkspace();
        LogService.Info("Git LFS enabled for workspace");
    }

    /// <summary>
    /// run `git lfs track` for each pattern, update .gitattributes in working tree.
    /// only adds patterns not already tracked.
    /// </summary>
    public void TrackLfsPatterns(IEnumerable<string> patterns)
    {
        var ws = _config.Workspace;
        var toAdd = patterns
            .Where(p => !ws.LfsTrackedPatterns.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var pattern in toAdd)
        {
            RunGitLfsCli($"track \"{pattern}\"");
            ws.LfsTrackedPatterns.Add(pattern);
            LogService.Info($"LFS tracking: {pattern}");
        }

        if (toAdd.Count > 0)
            _config.SaveWorkspace();
    }

    /// <summary>
    /// list local branch names (via git.exe so stale LibGit2Sharp DB isn't an issue).
    /// protected branches and the sentinel _init branch are filtered out.
    /// </summary>
    public List<string> GetMigratableLocalBranches()
    {
        var output = RunGitCli("branch --format=%(refname:short)");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !IsProtectedBranch(l) && l != InitBranch)
            .ToList();
    }

    /// <summary>
    /// rewrite branch history to replace blobs with LFS pointers, then force push.
    /// runs a single `migrate import` covering every branch in <paramref name="branches"/>,
    /// then checks out and force-pushes each branch in turn.
    /// for files that are already committed — use TrackLfsPatterns + CommitAndPush for new files instead.
    /// </summary>
    public void MigrateToLfsAndPush(IEnumerable<string> patterns, IReadOnlyList<string> branches)
    {
        if (branches.Count == 0)
            throw new InvalidOperationException("没有可迁移的分支");
        foreach (var b in branches)
            if (IsProtectedBranch(b))
                throw new InvalidOperationException($"不允许在受保护的分支上执行 LFS 迁移：{b}");

        var patternList = patterns.ToList();
        if (patternList.Count == 0)
            throw new InvalidOperationException("没有要迁移的文件");

        // sanity check: history rewrite + force push is a heavy operation, refuse if remote
        // doesn't match the configured workspace url
        using (var validateRepo = OpenRepo())
        {
            var ws = _config.Workspace;
            var origin = validateRepo.Network.Remotes["origin"];
            if (origin is null)
                throw new InvalidOperationException("Git 仓库未配置 origin 远端");
            if (!string.Equals(origin.Url, ws.RepoUrl, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"远端地址不一致，期望 \"{ws.RepoUrl}\"，实际为 \"{origin.Url}\"。请重新初始化配置。");
        }

        // build args manually — one --include= and one --include-ref= per item.
        // quoting each flag individually keeps paths/refs with spaces safe, and the
        // comma-separated form of --include would misparse anything containing a comma.
        var args = new StringBuilder("migrate import");
        foreach (var pat in patternList) args.Append($" \"--include={pat}\"");
        foreach (var br in branches) args.Append($" \"--include-ref=refs/heads/{br}\"");

        LogService.Info($"Starting LFS migration: {branches.Count} branch(es), {patternList.Count} pattern(s)");
        RunGitLfsCli(args.ToString(), timeout: 600_000);

        var savedBranch = GetCurrentBranch();

        foreach (var br in branches)
        {
            RunGitCli($"checkout \"{br}\"");

            // open fresh repo view (migrate rewrote history, previous SHA references are stale)
            using var repo = OpenRepo();
            var localTip = repo.Head.Tip?.Sha
                ?? throw new InvalidOperationException($"分支 {br} 没有任何提交，无法推送");

            LogService.Info($"LFS migration complete for {br}, force pushing");
            RunGitCli($"push --force-with-lease -u origin \"{br}\"", timeout: 300_000);
            VerifyPushResult(br, localTip);
            LogService.Info($"LFS migration push verified for {br}");
        }

        // restore original branch if it exists and wasn't in the migration set —
        // best-effort but log the reason so a corrupted repo / disk-full doesn't go silent
        if (!string.IsNullOrEmpty(savedBranch) && savedBranch != InitBranch && !branches.Contains(savedBranch))
        {
            try { RunGitCli($"checkout \"{savedBranch}\""); }
            catch (Exception ex) { LogService.Warn($"Failed to restore branch {savedBranch}: {ex.Message}"); }
        }
    }

    // detect LFS filter rules in .gitattributes and install git-lfs hooks if found.
    // when materialize is true, also runs `lfs checkout` so the working tree ends up with
    // real content instead of pointer text — required after reset / branch checkout, but
    // wasted work right after clone where the working tree is about to be wiped anyway.
    // failures here are non-fatal to the caller (clone/reset already succeeded) but the user
    // needs to know: we surface a visible warning via OnLfsMessage so at least
    // some UI lands instead of a silent log line.
    private void DetectAndInstallLfsIfNeeded(bool materialize = true)
    {
        var gitattributes = Path.Combine(WorkTreePath, ".gitattributes");
        if (!File.Exists(gitattributes)) return;
        if (!File.ReadAllText(gitattributes).Contains("filter=lfs")) return;

        try
        {
            RunGitLfsCli("install --local");

            if (materialize)
            {
                // materialize pointer files into real content — without this, freshly-reset
                // working trees still look like text files containing "version https://git-lfs..."
                try { RunGitLfsCli("checkout", timeout: 600_000); }
                catch (Exception chEx)
                {
                    LogService.Warn($"lfs checkout failed: {chEx.Message}");
                    OnLfsMessage?.Invoke(
                        "⚠ 大文件内容拉取失败，部分文件可能为占位符。请检查网络后再次点击拉取。");
                }
            }

            var ws = _config.Workspace;
            if (!ws.LfsEnabled)
            {
                ws.LfsEnabled = true;
                _config.SaveWorkspace();
            }
            LogService.Info("LFS auto-detected from .gitattributes and installed");
        }
        catch (Exception ex)
        {
            LogService.Warn($"LFS auto-detect failed: {ex.Message}");
            // surface to the user — a silent failure here means game reads pointer text and breaks
            OnLfsMessage?.Invoke(
                $"⚠ 检测到仓库使用大文件（LFS）但组件安装失败：{ex.Message}");
        }
    }

    /// <summary>
    /// rebuild each specified branch as an orphan commit and force-push to remote.
    /// this rewrites history, removing large files from all prior commits.
    /// calls onProgress(branch, index, total) before each branch.
    /// </summary>
    public List<OrphanResult> RebuildBranchesAsOrphan(List<string> branches, Action<string, int, int>? onProgress = null)
    {
        var results = new List<OrphanResult>();
        var filtered = branches
            .Where(b => !IsProtectedBranch(b) && b != InitBranch)
            .ToList();

        string? savedBranch = null;
        try { savedBranch = GetCurrentBranch(); } catch { }

        // try to unshallow first; silently ignore if already full clone
        try { RunGitCli("fetch --unshallow", timeout: 300_000); } catch { }

        for (int i = 0; i < filtered.Count; i++)
        {
            var branch = filtered[i];
            onProgress?.Invoke(branch, i, filtered.Count);
            try
            {
                RunGitCli($"checkout \"{branch}\"");
                RunGitCli("checkout --orphan __orphan_tmp");
                RunGitCli("reset");
                RunGitCli("add -A");
                RunGitCli($"commit -m \"Sync cleanup: rebuilt history\"");
                RunGitCli($"branch -D \"{branch}\"");
                RunGitCli($"branch -m \"{branch}\"");
                RunGitCli($"push --force-with-lease origin \"{branch}\"", timeout: 300_000);

                // verify push
                var localSha = RunGitCli("rev-parse HEAD").Trim();
                VerifyPushResult(branch, localSha);

                results.Add(new OrphanResult(branch, true, null));
                LogService.Info($"Orphan rebuilt: {branch}");
            }
            catch (Exception ex)
            {
                LogService.Warn($"Orphan rebuild failed for {branch}: {ex.Message}");
                results.Add(new OrphanResult(branch, false, ex.Message));
                // best-effort cleanup: HEAD may still point at __orphan_tmp, so `branch -D` would
                // refuse. step away from it first — prefer the target branch (it still exists if
                // the failure was before `branch -D <target>`), fall back to savedBranch, fall
                // back to any other branch we can find.
                try { RunGitCli($"checkout --force \"{branch}\""); }
                catch
                {
                    if (!string.IsNullOrEmpty(savedBranch))
                        try { RunGitCli($"checkout --force \"{savedBranch}\""); } catch { }
                }
                try { RunGitCli("branch -D __orphan_tmp"); } catch { }
            }
        }

        // one gc pass at the end to reclaim local space
        try { RunGitCli("gc --prune=now --aggressive", timeout: 600_000); } catch { }

        // restore original branch
        if (savedBranch != null && savedBranch != InitBranch)
            try { RunGitCli($"checkout \"{savedBranch}\""); } catch { }

        return results;
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

        // wipe all files from the clone dir so junction stays clean
        // (for non-junction mode, RepoPath is separate from WorkTreePath — clean it up too)
        foreach (var f in Directory.GetFiles(RepoPath))
            File.Delete(f);
        foreach (var d in Directory.GetDirectories(RepoPath))
            Directory.Delete(d, true);
    }

    private static Signature MakeSignature(WorkspaceConfig ws, string? gitEmail)
    {
        // for SSH/anonymous mode, nickname is the canonical identity
        var name = string.IsNullOrWhiteSpace(ws.Nickname) ? "player" : ws.Nickname;
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
        catch (Exception ex)
        {
            LogService.Warn($"Failed to read git global config '{key}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// fetch from origin: SSH always uses git.exe, HTTPS/anonymous try LibGit2Sharp first
    /// with git.exe fallback for platforms that LibGit2Sharp can't handle.
    /// wires up OnTransferProgress for real-time progress reporting when available.
    /// </summary>
    private void FetchAll(Repository repo)
    {
        if (IsSshMode)
        {
            RunGitCliWithProgress("fetch --all --prune --progress");
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

            // wire up LibGit2Sharp transfer progress → our callback
            if (OnTransferProgress != null)
            {
                var lastReport = Stopwatch.GetTimestamp();
                fetchOpts.OnTransferProgress = progress =>
                {
                    if (progress.TotalObjects == 0) return true;
                    var pct = (int)(progress.ReceivedObjects * 100L / progress.TotalObjects);
                    var elapsed = Stopwatch.GetElapsedTime(lastReport);
                    if (elapsed.TotalMilliseconds >= 200 || pct >= 100)
                    {
                        lastReport = Stopwatch.GetTimestamp();
                        var received = FormatBytes(progress.ReceivedBytes);
                        var detail = $"{pct}% ({progress.ReceivedObjects}/{progress.TotalObjects}, {received})";
                        OnTransferProgress(new GitTransferProgress(pct, detail));
                    }
                    return true;
                };
            }

            Commands.Fetch(repo, remote.Name, refSpecs, fetchOpts, null);
        }
        catch (LibGit2SharpException ex)
        {
            // fallback to git.exe for platforms with incompatible auth (e.g. Gitee)
            LogService.Warn($"LibGit2Sharp fetch failed, falling back to git.exe: {ex.Message}");
            RunGitCliFetch();
        }
    }

    /// <summary>
    /// run git.exe fetch with one retry on auth failure — credential helpers
    /// (e.g. GCM) may need a warm-up round on first invocation after app start.
    /// the 1s wait is rare (only on first auth failure) and we're already inside
    /// a Task.Run dispatched from MessageRouter, so blocking a thread-pool thread
    /// once per fetch retry is acceptable; converting the whole call chain to
    /// async would touch dozens of methods for negligible gain.
    /// </summary>
    private void RunGitCliFetch()
    {
        try
        {
            RunGitCliWithProgress("fetch --all --prune --progress");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Authentication failed"))
        {
            LogService.Warn("git.exe fetch auth failed, retrying after credential helper warm-up...");
            // sync sleep is intentional — see method-level comment above
            Thread.Sleep(1000);
            RunGitCliWithProgress("fetch --all --prune --progress");
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2}GiB",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2}MiB",
            >= 1024 => $"{bytes / 1024.0:F2}KiB",
            _ => $"{bytes}B"
        };
    }

    /// <summary>
    /// push current branch to origin: SSH always uses git.exe, HTTPS/anonymous try LibGit2Sharp
    /// first with git.exe fallback for platforms that LibGit2Sharp can't handle.
    /// validates remote URL before push and verifies the result after push.
    /// </summary>
    private void PushCurrentBranch(Repository repo)
    {
        // sanity check: make sure the repo's remote matches user config
        var ws = _config.Workspace;
        var remote = repo.Network.Remotes["origin"];
        if (remote is null)
            throw new InvalidOperationException("Git 仓库未配置 origin 远端");
        if (!string.Equals(remote.Url, ws.RepoUrl, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"远端地址不一致，期望 \"{ws.RepoUrl}\"，实际为 \"{remote.Url}\"。请重新初始化配置。");

        var localTip = repo.Head.Tip?.Sha
            ?? throw new InvalidOperationException("当前分支没有任何提交，无法推送");
        var branchName = repo.Head.FriendlyName;
        LogService.Info($"Pushing branch {branchName} to origin");

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
            catch (LibGit2SharpException ex)
            {
                // fallback to git.exe for platforms with incompatible auth (e.g. Gitee)
                LogService.Warn($"LibGit2Sharp push failed, falling back to git.exe: {ex.Message}");
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
        var ws = _config.Workspace;
        var remote = repo.Network.Remotes["origin"];
        if (remote is null)
            throw new InvalidOperationException("Git 仓库未配置 origin 远端");
        if (!string.Equals(remote.Url, ws.RepoUrl, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"远端地址不一致，期望 \"{ws.RepoUrl}\"，实际为 \"{remote.Url}\"。请重新初始化配置。");

        var localTip = repo.Head.Tip?.Sha
            ?? throw new InvalidOperationException("当前分支没有任何提交，无法推送");
        var branchName = repo.Head.FriendlyName;
        LogService.Info($"Force pushing branch {branchName} (--force-with-lease)");

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
        repo.Config.Set("core.worktree", WorkTreePath);
    }

    /// <summary>
    /// write exclude rules into git's info/exclude (same effect as .gitignore but invisible)
    /// </summary>
    private void EnsureExcludeRules()
    {
        var infoDir = Path.Combine(GitDirPath, "info");
        Directory.CreateDirectory(infoDir);
        var excludePath = Path.Combine(infoDir, "exclude");

        // merge-style write — preserve anything already in the file (user-added exclusion rules
        // from AppendExcludeRules must not be wiped). only append default lines that aren't present.
        var existing = new HashSet<string>(
            File.Exists(excludePath) ? File.ReadAllLines(excludePath) : [],
            StringComparer.OrdinalIgnoreCase);

        var toAdd = DefaultExcludeRules
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !existing.Contains(l))
            .ToList();

        if (toAdd.Count == 0) return;

        using var writer = File.AppendText(excludePath);
        foreach (var l in toAdd)
            writer.WriteLine(l);
    }

    /// <summary>
    /// remove .gitignore / .gitattributes etc from working tree if they came from the remote
    /// </summary>
    // use git.exe when LFS is enabled so the clean filter runs and files become LFS pointers
    private void StageAll(Repository repo)
    {
        if (_config.Workspace.LfsEnabled)
            RunGitCli("add -A");
        else
            Commands.Stage(repo, "*");
    }

    private void CleanGitArtifactsFromWorkTree()
    {
        // always remove .gitignore — we manage exclusions via info/exclude
        var gitignore = Path.Combine(WorkTreePath, ".gitignore");
        if (File.Exists(gitignore)) File.Delete(gitignore);

        // keep .gitattributes when LFS is active — it holds the filter rules
        if (!_config.Workspace.LfsEnabled)
        {
            var gitattributes = Path.Combine(WorkTreePath, ".gitattributes");
            if (File.Exists(gitattributes)) File.Delete(gitattributes);
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

    // L3: split a command string respecting double-quoted segments
    private static List<string> SplitArgs(string input)
    {
        var result = new List<string>();
        var i = 0;
        while (i < input.Length)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }
            if (input[i] == '"')
            {
                var end = input.IndexOf('"', i + 1);
                if (end == -1) end = input.Length;
                result.Add(input[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                var end = input.IndexOf(' ', i);
                if (end == -1) end = input.Length;
                result.Add(input[i..end]);
                i = end;
            }
        }
        return result;
    }
}
