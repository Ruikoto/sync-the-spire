using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

public class GitService
{
    private readonly ConfigService _config;

    // stuff we never want committed
    private const string DefaultGitIgnore =
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

    private CredentialsHandler MakeCredHandler()
    {
        var cfg = _config.LoadConfig();
        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = cfg.Username,
            Password = cfg.Token
        };
    }

    // ── clone ────────────────────────────────────────────────────────────

    public void CloneRepo()
    {
        var cfg = _config.LoadConfig();
        var opts = new CloneOptions();
        opts.FetchOptions.CredentialsProvider = MakeCredHandler();

        Repository.Clone(cfg.RepoUrl, RepoPath, opts);

        // drop a sensible .gitignore into the repo
        EnsureGitIgnore();
    }

    // ── branch queries ───────────────────────────────────────────────────

    public string GetCurrentBranch()
    {
        using var repo = new Repository(RepoPath);
        return repo.Head.FriendlyName;
    }

    public List<string> GetRemoteBranches()
    {
        using var repo = new Repository(RepoPath);

        // fetch first so we have the latest refs
        FetchAllInternal(repo);

        var remote = repo.Network.Remotes["origin"];
        if (remote is null) return [];

        return repo.Refs
            .Where(r => r.CanonicalName.StartsWith("refs/remotes/origin/") &&
                        !r.CanonicalName.EndsWith("/HEAD"))
            .Select(r => r.CanonicalName.Replace("refs/remotes/origin/", ""))
            .ToList();
    }

    public bool HasLocalChanges()
    {
        using var repo = new Repository(RepoPath);
        var status = repo.RetrieveStatus(new StatusOptions());
        return status.IsDirty;
    }

    // ── sync (mode 2 - force checkout remote branch) ────────────────────

    public void ForceCheckoutBranch(string branchName)
    {
        using var repo = new Repository(RepoPath);

        FetchAllInternal(repo);

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
        using var repo = new Repository(RepoPath);

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

        repo.Network.Push(newBranch, new PushOptions
        {
            CredentialsProvider = MakeCredHandler()
        });
    }

    public void CommitAndPush()
    {
        using var repo = new Repository(RepoPath);

        // stage everything
        Commands.Stage(repo, "*");

        var status = repo.RetrieveStatus(new StatusOptions());
        if (!status.IsDirty)
            return; // nothing to commit

        var cfg = _config.LoadConfig();
        var sig = new Signature(cfg.Username, $"{cfg.Username}@sync-the-spire", DateTimeOffset.Now);
        repo.Commit("Auto-save", sig, sig);

        var currentBranch = repo.Head;
        repo.Network.Push(currentBranch, new PushOptions
        {
            CredentialsProvider = MakeCredHandler()
        });
    }

    /// <summary>
    /// silently commit any uncommitted work before switching away
    /// </summary>
    public void SilentCommitIfDirty()
    {
        using var repo = new Repository(RepoPath);
        Commands.Stage(repo, "*");

        var status = repo.RetrieveStatus(new StatusOptions());
        if (!status.IsDirty) return;

        var cfg = _config.LoadConfig();
        var sig = new Signature(cfg.Username, $"{cfg.Username}@sync-the-spire", DateTimeOffset.Now);
        repo.Commit("Auto-save (before switch)", sig, sig);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private void FetchAllInternal(Repository repo)
    {
        var remote = repo.Network.Remotes["origin"];
        if (remote is null) return;

        var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
        Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
        {
            CredentialsProvider = MakeCredHandler()
        }, null);
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
            // don't touch .git
            if (Path.GetFileName(dir) == ".git") continue;

            RemoveEmptyDirs(dir);
            if (Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
    }

    private void EnsureGitIgnore()
    {
        var gitIgnorePath = Path.Combine(RepoPath, ".gitignore");
        if (File.Exists(gitIgnorePath)) return;

        File.WriteAllText(gitIgnorePath, DefaultGitIgnore);

        // commit the .gitignore
        using var repo = new Repository(RepoPath);
        Commands.Stage(repo, ".gitignore");

        var cfg = _config.LoadConfig();
        var sig = new Signature(cfg.Username, $"{cfg.Username}@sync-the-spire", DateTimeOffset.Now);
        repo.Commit("Add .gitignore", sig, sig);
    }
}
