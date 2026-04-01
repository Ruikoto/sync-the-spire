using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Handlers;
using SyncTheSpire.Helpers;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Services;

public class MessageRouter
{
    private readonly CoreWebView2 _webView;
    private readonly WorkspaceManager _workspaceManager;
    private readonly MainForm _form;
    private readonly SynchronizationContext _uiContext;
    private readonly GitResolver _gitResolver;
    private readonly JunctionService _junctionService;
    private readonly StoreUpdateService _storeUpdateService;
    // only one IPC operation at a time to prevent concurrent access to git/config/filesystem
    private readonly SemaphoreSlim _gate = new(1, 1);

    // domain handlers — rebuilt when workspace changes
    private ConfigHandler _configHandler = null!;
    private GitBranchHandler _gitBranchHandler = null!;
    private SaveHandler _saveHandler = null!;
    private RedirectHandler _redirectHandler = null!;
    private AnnouncementHandler _announcementHandler = null!;
    private StoreUpdateHandler _storeUpdateHandler = null!;
    private SteamFinderHandler _steamFinderHandler = null!;
    private FilesystemHandler _filesystemHandler = null!;

    // current workspace context (null if no workspace active yet)
    private WorkspaceContext? _currentContext;

    public MessageRouter(
        CoreWebView2 webView,
        WorkspaceManager workspaceManager,
        WorkspaceContext? initialContext,
        GitResolver gitResolver,
        JunctionService junctionService,
        StoreUpdateService storeUpdateService,
        MainForm form)
    {
        _webView = webView;
        _workspaceManager = workspaceManager;
        _form = form;
        _gitResolver = gitResolver;
        _junctionService = junctionService;
        _storeUpdateService = storeUpdateService;
        // capture the UI SynchronizationContext so background threads can post back
        _uiContext = SynchronizationContext.Current
                     ?? throw new InvalidOperationException("MessageRouter must be created on the UI thread");

        // wire up MinGit download progress to IPC loading overlay
        gitResolver.OnProgress = p =>
            Send(IpcResponse.Progress("GIT_DOWNLOAD", p.Message, p.Percent));

        // init handlers with current workspace context (may be null for fresh install)
        _currentContext = initialContext;
        BuildHandlers();
    }

    /// <summary>
    /// Rebuild all domain handlers for the current workspace context.
    /// Called on init and whenever the active workspace changes.
    /// </summary>
    private void BuildHandlers()
    {
        var ctx = _currentContext;

        // these two don't need workspace context
        _storeUpdateHandler = new StoreUpdateHandler(_webView, _uiContext, _storeUpdateService);
        _steamFinderHandler = new SteamFinderHandler(_webView, _uiContext, new SteamFinderService());

        if (ctx != null)
        {
            var junctionHelper = new JunctionHelper(_junctionService, ctx.BackupService, Send);

            _configHandler = new ConfigHandler(_webView, _uiContext, ctx.ConfigService, ctx.GitService, _junctionService, junctionHelper);
            _gitBranchHandler = new GitBranchHandler(_webView, _uiContext, ctx.ConfigService, ctx.GitService, _junctionService, junctionHelper);
            _saveHandler = new SaveHandler(_webView, _uiContext, ctx.ConfigService, ctx.BackupService, ctx.MergeService, _junctionService);
            _redirectHandler = new RedirectHandler(_webView, _uiContext, ctx.ConfigService, _junctionService);
            _announcementHandler = new AnnouncementHandler(_webView, _uiContext, ctx.ConfigService);
            _filesystemHandler = new FilesystemHandler(_webView, _uiContext, ctx.ConfigService, _junctionService, ctx.BackupService, junctionHelper, _form);
        }
        else
        {
            // no workspace active — create stub handlers with a temporary config service
            // so GET_STATUS can report "not configured" and GET_VERSION still works
            var stubWs = new WorkspaceConfig();
            var stubCs = new ConfigService(stubWs, _workspaceManager, "", "");
            var stubBackup = new SaveBackupService(Path.Combine(WorkspaceManager.AppDataDir, "Backups"));
            var stubJH = new JunctionHelper(_junctionService, stubBackup, Send);

            _configHandler = new ConfigHandler(_webView, _uiContext, stubCs, null!, _junctionService, stubJH);
            _gitBranchHandler = null!;
            _saveHandler = null!;
            _redirectHandler = new RedirectHandler(_webView, _uiContext, stubCs, _junctionService);
            _announcementHandler = new AnnouncementHandler(_webView, _uiContext, stubCs);
            _filesystemHandler = new FilesystemHandler(_webView, _uiContext, stubCs, _junctionService, stubBackup, stubJH, _form);
        }
    }

    public void HandleMessage(string rawJson)
    {
        IpcRequest? req;
        try
        {
            // WebView2 sends WebMessageAsJson which is already a JSON string,
            // but it wraps plain strings in quotes. Our frontend sends JSON objects
            // so we may need to unwrap one layer if it got double-serialized.
            var trimmed = rawJson.Trim();
            if (trimmed.StartsWith("\""))
            {
                // double-encoded string from WebView2, unwrap first
                var inner = JsonSerializer.Deserialize<string>(trimmed);
                req = JsonSerializer.Deserialize<IpcRequest>(inner!);
            }
            else
            {
                req = JsonSerializer.Deserialize<IpcRequest>(trimmed);
            }
        }
        catch
        {
            LogService.Warn($"Invalid IPC message: {rawJson}");
            Send(IpcResponse.Error("UNKNOWN", "Invalid JSON"));
            return;
        }

        if (req is null)
        {
            Send(IpcResponse.Error("UNKNOWN", "Empty request"));
            return;
        }

        if (req.Action != "WINDOW_DRAG")
            LogService.Info($"IPC <- {req.Action}");

        // run everything off the UI thread so we don't freeze the window
        Task.Run(() => Route(req));
    }

    private void Route(IpcRequest req)
    {
        // window chrome controls don't need serialization — fire and forget on UI thread
        switch (req.Action)
        {
            case "WINDOW_DRAG":
                _uiContext.Post(_ => _form.BeginDrag(), null);
                return;
            case "WINDOW_MINIMIZE":
                _uiContext.Post(_ => _form.WindowState = FormWindowState.Minimized, null);
                return;
            case "WINDOW_MAXIMIZE":
                _uiContext.Post(_ =>
                    _form.WindowState = _form.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized, null);
                return;
            case "WINDOW_CLOSE":
                _uiContext.Post(_ => _form.Close(), null);
                return;
            // PICK_FOLDER needs UI dialog — handle outside the gate to avoid blocking other IPC
            case "PICK_FOLDER":
                _filesystemHandler.HandlePickFolder();
                return;
            // Store update actions use system UI / network calls — don't hold the gate
            case "CHECK_STORE_UPDATE":
                _ = _storeUpdateHandler.HandleCheckStoreUpdate();
                return;
            case "INSTALL_STORE_UPDATE":
                _ = _storeUpdateHandler.HandleInstallStoreUpdate();
                return;
            // mod preview reads immutable git objects — safe outside the gate
            case "GET_BRANCH_MODS":
                _gitBranchHandler?.HandleGetBranchMods(req.Payload);
                return;
            // steam auto-find — read-only filesystem/registry, no need for the gate
            case "FIND_GAME_PATH":
                _steamFinderHandler.HandleFindGamePath();
                return;
            case "FIND_SAVE_PATH":
                _steamFinderHandler.HandleFindSavePath();
                return;
        }

        _gate.Wait();
        try
        {
            switch (req.Action)
            {
                case "GET_STATUS":
                    _configHandler.HandleGetStatus();
                    break;

                case "REFRESH_SYNC":
                    _configHandler.HandleRefreshSync();
                    break;

                case "GET_VERSION":
                    _configHandler.HandleGetVersion();
                    break;

                case "GET_CONFIG":
                    _configHandler.HandleGetConfig();
                    break;

                case "INIT_CONFIG":
                    HandleInitConfigWithWorkspace(req.Payload);
                    break;

                case "GET_BRANCHES":
                    _gitBranchHandler.HandleGetBranches();
                    break;

                case "SWITCH_TO_VANILLA":
                    _gitBranchHandler.HandleSwitchToVanilla();
                    break;

                case "SYNC_OTHER_BRANCH":
                    _gitBranchHandler.HandleSyncOtherBranch(req.Payload);
                    break;

                case "CREATE_MY_BRANCH":
                    _gitBranchHandler.HandleCreateMyBranch(req.Payload);
                    break;

                case "SAVE_AND_PUSH_MY_BRANCH":
                    _gitBranchHandler.HandleSaveAndPush();
                    break;

                case "FORCE_PUSH":
                    _gitBranchHandler.HandleForcePush();
                    break;

                case "RESET_TO_REMOTE":
                    _gitBranchHandler.HandleResetToRemote();
                    break;

                case "RESTORE_JUNCTION":
                    _filesystemHandler.HandleRestoreJunction();
                    break;

                case "OPEN_FOLDER":
                    _filesystemHandler.HandleOpenFolder(req.Payload);
                    break;

                // ── save management ──────────────────────────────────
                case "GET_SAVE_STATUS":
                    _saveHandler.HandleGetSaveStatus();
                    break;

                case "UNLINK_SAVES":
                    _saveHandler.HandleUnlinkSaves();
                    break;

                case "BACKUP_SAVES":
                    _saveHandler.HandleBackupSaves();
                    break;

                case "GET_BACKUP_LIST":
                    _saveHandler.HandleGetBackupList();
                    break;

                case "RESTORE_BACKUP":
                    _saveHandler.HandleRestoreBackup(req.Payload);
                    break;

                case "DELETE_BACKUP":
                    _saveHandler.HandleDeleteBackup(req.Payload);
                    break;

                // ── save redirect ─────────────────────────────────────
                case "GET_REDIRECT_STATUS":
                    _redirectHandler.HandleGetRedirectStatus();
                    break;

                case "SET_REDIRECT":
                    _redirectHandler.HandleSetRedirect(req.Payload);
                    break;

                case "GET_DISMISSED_ANNOUNCEMENTS":
                    _announcementHandler.HandleGetDismissedAnnouncements();
                    break;

                case "DISMISS_ANNOUNCEMENT":
                    _announcementHandler.HandleDismissAnnouncement(req.Payload);
                    break;

                default:
                    Send(IpcResponse.Error(req.Action, $"Unknown action: {req.Action}"));
                    break;
            }
        }
        catch (IOException ex)
        {
            // most likely file-in-use by game process
            LogService.Error($"[{req.Action}] IO error", ex);
            Send(IpcResponse.Error(req.Action, $"文件被占用，请先关闭游戏再操作！\n{ex.Message}"));
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            LogService.Error($"[{req.Action}] LibGit2Sharp error", ex);
            Send(IpcResponse.Error(req.Action, $"Git 操作失败：{ex.Message}"));
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("401") || ex.Message.Contains("403"))
        {
            LogService.Error($"[{req.Action}] Auth failure", ex);
            var authType = _currentContext?.ConfigService.LoadConfig().AuthType ?? "anonymous";
            var msg = authType switch
            {
                "anonymous" =>
                    "仓库认证失败，请重试或切换到其他认证方式。",
                "https" =>
                    "仓库认证失败，请检查用户名和 Token 是否正确。",
                _ => $"Git 认证失败：{ex.Message}"
            };
            Send(IpcResponse.Error(req.Action, msg));
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("git") && ex.Message.Contains("failed"))
        {
            LogService.Error($"[{req.Action}] Git CLI failure", ex);
            Send(IpcResponse.Error(req.Action, $"Git 操作失败：{ex.Message}"));
        }
        catch (Exception ex)
        {
            LogService.Error($"[{req.Action}] Unexpected error", ex);
            Send(IpcResponse.Error(req.Action, $"操作失败：{ex.Message}"));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Intercepts INIT_CONFIG: if no workspace exists yet, create one first,
    /// then rebuild handlers with the new workspace context before delegating.
    /// </summary>
    private void HandleInitConfigWithWorkspace(JsonElement? payload)
    {
        if (_currentContext == null)
        {
            // first-time setup — create a default workspace
            var ws = _workspaceManager.CreateWorkspace("Slay the Spire 2", "sts2");
            _workspaceManager.SetActiveWorkspace(ws.Id);

            _currentContext = new WorkspaceContext(ws, _workspaceManager, _gitResolver, _junctionService);
            BuildHandlers();
        }

        _configHandler.HandleInitConfig(payload);
    }

    private void Send(IpcResponse response)
    {
        var json = response.ToJson();
        // PostWebMessageAsString must be called on the UI thread
        _uiContext.Post(_ => _webView.PostWebMessageAsString(json), null);
    }
}
