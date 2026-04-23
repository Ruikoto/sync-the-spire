using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

/// <summary>
/// Workspace-scoped config wrapper.
/// Exposes the underlying WorkspaceConfig directly and delegates persistence to WorkspaceManager.
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
    /// Direct access to the underlying WorkspaceConfig.
    /// </summary>
    public WorkspaceConfig Workspace => _workspace;

    /// <summary>
    /// global app settings (language, file size limits, etc.)
    /// </summary>
    public AppSettings Settings => _manager.Config.Settings;

    /// <summary>
    /// persist the current workspace config to disk
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
