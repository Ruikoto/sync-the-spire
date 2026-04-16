using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

/// <summary>
/// Workspace-scoped config wrapper.
/// Presents a WorkspaceConfig through the familiar AppConfig interface so
/// that existing handlers and GitService keep working without changes.
/// Delegates persistence to WorkspaceManager.
/// </summary>
public class ConfigService
{
    // still used by a few places that need the global app data dir (WebView2 UDF, etc.)
    public static readonly string AppDataDirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncTheSpire");

    private readonly WorkspaceConfig _workspace;
    private readonly WorkspaceManager _manager;

    public string RepoPath { get; }
    public string GitDirPath { get; }
    public string WorkTreePath { get; }

    public bool IsRepoInitialized => Directory.Exists(GitDirPath) &&
                                     Directory.Exists(Path.Combine(GitDirPath, "objects"));

    public ConfigService(WorkspaceConfig workspace, WorkspaceManager manager, string repoPath, string gitDirPath, string workTreePath)
    {
        _workspace = workspace;
        _manager = manager;
        RepoPath = repoPath;
        GitDirPath = gitDirPath;
        WorkTreePath = workTreePath;
    }

    /// <summary>
    /// Direct access to the underlying WorkspaceConfig — preferred over LoadConfig().
    /// </summary>
    public WorkspaceConfig Workspace => _workspace;

    /// <summary>
    /// Returns an AppConfig view of the current workspace config.
    /// Legacy bridge — prefer using Workspace directly for correct GameModPath.
    /// </summary>
    public AppConfig LoadConfig()
    {
        return new AppConfig
        {
            RepoUrl = _workspace.RepoUrl,
            Nickname = _workspace.Nickname,
            Username = _workspace.Username,
            Token = _workspace.Token,
            AuthType = _workspace.AuthType,
            SshKeyPath = _workspace.SshKeyPath,
            SshPassphrase = _workspace.SshPassphrase,
            GameInstallPath = _workspace.GameInstallPath,
            GameModPathLegacy = _workspace.GameModPathLegacy,
            SaveFolderPath = _workspace.SaveFolderPath,
        };
    }

    /// <summary>
    /// Saves changes from an AppConfig back into the workspace config and persists to disk.
    /// </summary>
    public void SaveConfig(AppConfig config)
    {
        _workspace.RepoUrl = config.RepoUrl;
        _workspace.Nickname = config.Nickname;
        _workspace.Username = config.Username;
        _workspace.Token = config.Token;
        _workspace.AuthType = config.AuthType;
        _workspace.SshKeyPath = config.SshKeyPath;
        _workspace.SshPassphrase = config.SshPassphrase;
        _workspace.GameInstallPath = config.GameInstallPath;
        _workspace.GameModPathLegacy = config.GameModPathLegacy;
        _workspace.SaveFolderPath = config.SaveFolderPath;

        _manager.SaveConfig();
        LogService.Info("Workspace config saved");
    }

    /// <summary>
    /// no-op in workspace mode — config is always in-memory from WorkspaceManager
    /// </summary>
    public void InvalidateCache() { }

    /// <summary>
    /// persist the current workspace config to disk (for direct field modifications)
    /// </summary>
    public void SaveWorkspace()
    {
        _manager.SaveConfig();
        LogService.Info("Workspace config saved");
    }

    // ── dismissed announcements (delegate to WorkspaceManager, global) ───

    public List<string> GetDismissedAnnouncements() => _manager.GetDismissedAnnouncements();
    public void DismissAnnouncement(string id) => _manager.DismissAnnouncement(id);
}
