using SyncTheSpire.Adapters;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

/// <summary>
/// Per-workspace service container. Holds workspace-scoped services and paths.
/// Created by WorkspaceManager, disposed when switching away or deleting.
/// </summary>
public class WorkspaceContext : IDisposable
{
    public string WorkspaceId { get; }
    public WorkspaceConfig Config { get; }
    public IGameAdapter GameAdapter { get; }

    // workspace-scoped services
    public ConfigService ConfigService { get; }
    public GitService GitService { get; }
    public SaveBackupService BackupService { get; }
    public SaveMergeService MergeService { get; }

    // workspace-scoped paths
    public string RepoPath { get; }
    public string GitDirPath { get; }
    public string BackupDir { get; }

    public WorkspaceContext(
        WorkspaceConfig config,
        WorkspaceManager manager,
        GitResolver gitResolver,
        JunctionService junctionService)
    {
        WorkspaceId = config.Id;
        Config = config;
        GameAdapter = GameAdapterRegistry.Get(config.GameType);

        RepoPath = manager.GetRepoPath(config.Id);
        GitDirPath = manager.GetGitDirPath(config.Id);
        BackupDir = manager.GetBackupDir(config.Id);

        // create workspace-scoped services
        ConfigService = new ConfigService(config, manager, RepoPath, GitDirPath);
        BackupService = new SaveBackupService(BackupDir);
        GitService = new GitService(ConfigService, gitResolver);
        MergeService = new SaveMergeService(junctionService, BackupService, GameAdapter);
    }

    public void Dispose()
    {
        // nothing to dispose right now, but future-proof for LibGit2Sharp repo handles etc.
        GC.SuppressFinalize(this);
    }
}
