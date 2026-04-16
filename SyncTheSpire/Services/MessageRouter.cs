using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Adapters;
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

    // workspace handler — lives outside the per-workspace lifecycle
    private WorkspaceHandler _workspaceHandler = null!;
    private SettingsHandler _settingsHandler = null!;

    // domain handlers — rebuilt when workspace changes
    private ConfigHandler _configHandler = null!;
    private GitBranchHandler _gitBranchHandler = null!;
    private SaveHandler _saveHandler = null!;
    private RedirectHandler _redirectHandler = null!;
    private AnnouncementHandler _announcementHandler = null!;
    private StoreUpdateHandler _storeUpdateHandler = null!;
    private SteamFinderHandler _steamFinderHandler = null!;
    private FilesystemHandler _filesystemHandler = null!;
    private ModOrderHandler _modOrderHandler = null!;

    // A3: actions that require an active workspace context — unified null guard
    private static readonly HashSet<string> WorkspaceScopedActions =
    [
        "GET_STATUS", "REFRESH_SYNC", "GET_CONFIG", "INIT_CONFIG",
        "GET_BRANCHES", "SWITCH_TO_VANILLA", "SYNC_OTHER_BRANCH",
        "CREATE_MY_BRANCH", "SAVE_AND_PUSH_MY_BRANCH", "FORCE_PUSH", "RESET_TO_REMOTE",
        "GET_SAVE_STATUS", "UNLINK_SAVES", "BACKUP_SAVES", "GET_BACKUP_LIST", "RESTORE_BACKUP", "DELETE_BACKUP",
        "GET_REDIRECT_STATUS", "SET_REDIRECT",
        "RESTORE_JUNCTION", "OPEN_FOLDER",
        "LAUNCH_GAME", "SET_CUSTOM_EXE",
        "GET_MOD_ORDER", "SAVE_MOD_ORDER",
    ];

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

        // these don't need workspace context
        _workspaceHandler = new WorkspaceHandler(_webView, _uiContext, _workspaceManager);
        _settingsHandler = new SettingsHandler(_webView, _uiContext, _workspaceManager);
        _storeUpdateHandler = new StoreUpdateHandler(_webView, _uiContext, _storeUpdateService);

        if (ctx != null)
        {
            var adapter = ctx.GameAdapter;
            var junctionHelper = new JunctionHelper(_junctionService, ctx.BackupService, Send);

            _configHandler = new ConfigHandler(_webView, _uiContext, ctx.ConfigService, ctx.GitService, _junctionService, junctionHelper, adapter, _workspaceManager);
            _gitBranchHandler = new GitBranchHandler(_webView, _uiContext, ctx.ConfigService, ctx.GitService, _junctionService, junctionHelper, adapter);
            _saveHandler = new SaveHandler(_webView, _uiContext, ctx.ConfigService, ctx.BackupService, ctx.MergeService, _junctionService);
            _redirectHandler = new RedirectHandler(_webView, _uiContext, ctx.ConfigService, _junctionService, adapter);
            _announcementHandler = new AnnouncementHandler(_webView, _uiContext, ctx.ConfigService);
            _steamFinderHandler = new SteamFinderHandler(_webView, _uiContext, adapter);
            _filesystemHandler = new FilesystemHandler(_webView, _uiContext, ctx.ConfigService, _junctionService, ctx.BackupService, junctionHelper, adapter, _form);
            _modOrderHandler = new ModOrderHandler(_webView, _uiContext, ctx.ConfigService, ctx.GitService, adapter);
        }
        else
        {
            // no workspace active — create stub handlers with a temporary config service
            // so GET_STATUS can report "not configured" and GET_VERSION still works
            var stubWs = new WorkspaceConfig();
            var stubCs = new ConfigService(stubWs, _workspaceManager, "", "", "");
            var stubBackup = new SaveBackupService(Path.Combine(WorkspaceManager.AppDataDir, "Backups"));
            var stubJH = new JunctionHelper(_junctionService, stubBackup, Send);
            var stubAdapter = GameAdapterRegistry.Get("sts2");

            _configHandler = new ConfigHandler(_webView, _uiContext, stubCs, null!, _junctionService, stubJH, stubAdapter, _workspaceManager);
            _gitBranchHandler = null!;
            _saveHandler = null!;
            _redirectHandler = new RedirectHandler(_webView, _uiContext, stubCs, _junctionService, stubAdapter);
            _announcementHandler = new AnnouncementHandler(_webView, _uiContext, stubCs);
            _steamFinderHandler = new SteamFinderHandler(_webView, _uiContext, stubAdapter);
            _filesystemHandler = new FilesystemHandler(_webView, _uiContext, stubCs, _junctionService, stubBackup, stubJH, stubAdapter, _form);
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

        if (req.Action != "WINDOW_DRAG" && req.Action != "WINDOW_RESIZE")
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
            case "WINDOW_RESIZE":
                var edge = req.Payload?.GetProperty("edge").GetString() ?? "";
                _uiContext.Post(_ => _form.BeginResize(edge), null);
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
            case "PICK_GAME_EXE":
                _filesystemHandler.HandlePickGameExe();
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
                // H2 fix: send error instead of silently dropping when no workspace
                if (_gitBranchHandler is null)
                {
                    Send(IpcResponse.Error("GET_BRANCH_MODS", "当前没有活跃的工作区"));
                    return;
                }
                _gitBranchHandler.HandleGetBranchMods(req.Payload);
                return;
            case "GET_MOD_DIFF":
                if (_gitBranchHandler is null)
                {
                    Send(IpcResponse.Error("GET_MOD_DIFF", "当前没有活跃的工作区"));
                    return;
                }
                _gitBranchHandler.HandleGetModDiff();
                return;
            // steam auto-find — read-only filesystem/registry, no need for the gate
            case "FIND_GAME_PATH":
                _steamFinderHandler.HandleFindGamePath();
                return;
            case "FIND_SAVE_PATH":
                _steamFinderHandler.HandleFindSavePath();
                return;
            // workspace queries — read-only, no gate needed
            case "GET_WORKSPACES":
                _workspaceHandler.HandleGetWorkspaces();
                return;
            case "GET_GAME_TYPES":
                _workspaceHandler.HandleGetGameTypes();
                return;
        }

        _gate.Wait();
        try
        {
            // A3: unified null-workspace interception — INIT_CONFIG is exempt (creates workspace)
            if (_currentContext == null && req.Action != "INIT_CONFIG" && WorkspaceScopedActions.Contains(req.Action))
            {
                Send(IpcResponse.Error(req.Action, "请先选择一个工作区"));
                return;
            }

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

                case "LAUNCH_GAME":
                    _filesystemHandler.HandleLaunchGame();
                    break;

                case "SET_CUSTOM_EXE":
                    _filesystemHandler.HandleSetCustomExe(req.Payload);
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

                // ── mod load order ───────────────────────────────────
                case "GET_MOD_ORDER":
                    _modOrderHandler.HandleGetModOrder();
                    break;

                case "SAVE_MOD_ORDER":
                    _modOrderHandler.HandleSaveModOrder(req.Payload);
                    break;

                case "GET_DISMISSED_ANNOUNCEMENTS":
                    _announcementHandler.HandleGetDismissedAnnouncements();
                    break;

                case "DISMISS_ANNOUNCEMENT":
                    _announcementHandler.HandleDismissAnnouncement(req.Payload);
                    break;

                // ── workspace management ─────────────────────────────
                case "CREATE_WORKSPACE":
                    HandleCreateWorkspaceAndSwitch(req.Payload);
                    break;

                case "DELETE_WORKSPACE":
                    HandleDeleteWorkspaceAndSwitch(req.Payload);
                    break;

                case "SWITCH_WORKSPACE":
                    HandleSwitchWorkspace(req.Payload);
                    break;

                case "OPEN_WORKSPACE_TAB":
                    _workspaceHandler.HandleOpenTab(req.Payload);
                    break;

                case "CLOSE_WORKSPACE_TAB":
                    HandleCloseTabAndMaybeSwitch(req.Payload);
                    break;

                case "RENAME_WORKSPACE":
                    _workspaceHandler.HandleRenameWorkspace(req.Payload);
                    break;

                // ── global settings ───────────────────────────────
                case "GET_SETTINGS":
                    _settingsHandler.HandleGetSettings();
                    break;

                case "SAVE_SETTINGS":
                    _settingsHandler.HandleSaveSettings(req.Payload);
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

    // CREATE_WORKSPACE — create, switch context, notify
    private void HandleCreateWorkspaceAndSwitch(JsonElement? payload)
    {
        // M2 fix: safe property access
        string? name = null, gameType = "sts2";
        if (payload is not null)
        {
            if (payload.Value.TryGetProperty("name", out var nameEl)) name = nameEl.GetString();
            if (payload.Value.TryGetProperty("gameType", out var gtEl)) gameType = gtEl.GetString() ?? "sts2";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            Send(IpcResponse.Error("CREATE_WORKSPACE", "工作区名称不能为空"));
            return;
        }

        // block coming-soon game types in release builds
        var adapter = GameAdapterRegistry.Get(gameType);
        var ver = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        if (!ver.StartsWith("nightly-") && adapter.ComingSoon)
        {
            Send(IpcResponse.Error("CREATE_WORKSPACE", "该游戏类型尚未开放"));
            return;
        }

        var ws = _workspaceManager.CreateWorkspace(name, gameType);
        _workspaceManager.SetActiveWorkspace(ws.Id);

        // switch context to the new workspace
        _currentContext?.Dispose();
        _currentContext = new WorkspaceContext(ws, _workspaceManager, _gitResolver, _junctionService);
        BuildHandlers();

        Send(IpcResponse.Success("CREATE_WORKSPACE", new
        {
            id = ws.Id,
            name = ws.Name,
            gameType = ws.GameType,
            gameDisplayName = GameAdapterRegistry.Get(ws.GameType).DisplayName,
            openTabs = _workspaceManager.Config.OpenTabs,
            activeWorkspace = _workspaceManager.Config.ActiveWorkspace,
        }));
    }

    // SWITCH_WORKSPACE — dispose old context, create new one, rebuild handlers
    private void HandleSwitchWorkspace(JsonElement? payload)
    {
        string? id = null;
        if (payload is not null && payload.Value.TryGetProperty("id", out var idEl))
            id = idEl.GetString();
        var ws = string.IsNullOrWhiteSpace(id) ? null : _workspaceManager.GetWorkspace(id);
        if (ws == null)
        {
            Send(IpcResponse.Error("SWITCH_WORKSPACE", "工作区不存在"));
            return;
        }

        _currentContext?.Dispose();
        _workspaceManager.SetActiveWorkspace(id!);
        _currentContext = new WorkspaceContext(ws, _workspaceManager, _gitResolver, _junctionService);
        BuildHandlers();

        Send(IpcResponse.Success("SWITCH_WORKSPACE", new
        {
            id = ws.Id,
            name = ws.Name,
            gameType = ws.GameType,
            gameDisplayName = GameAdapterRegistry.Get(ws.GameType).DisplayName,
        }));
    }

    // DELETE_WORKSPACE — dispose context if active, delete, rebuild if needed
    private void HandleDeleteWorkspaceAndSwitch(JsonElement? payload)
    {
        string? id = null;
        if (payload is not null && payload.Value.TryGetProperty("id", out var idEl))
            id = idEl.GetString();
        if (string.IsNullOrWhiteSpace(id))
        {
            Send(IpcResponse.Error("DELETE_WORKSPACE", "缺少工作区 ID"));
            return;
        }

        // if deleting the active workspace, tear down context first
        if (_workspaceManager.Config.ActiveWorkspace == id)
        {
            _currentContext?.Dispose();
            _currentContext = null;
        }

        _workspaceManager.DeleteWorkspace(id);

        // if there's a new active workspace after deletion, spin up its context
        var newActive = _workspaceManager.Config.ActiveWorkspace;
        if (newActive != null)
        {
            var ws = _workspaceManager.GetWorkspace(newActive);
            if (ws != null)
            {
                _currentContext = new WorkspaceContext(ws, _workspaceManager, _gitResolver, _junctionService);
            }
        }
        BuildHandlers();

        Send(IpcResponse.Success("DELETE_WORKSPACE", new
        {
            openTabs = _workspaceManager.Config.OpenTabs,
            activeWorkspace = _workspaceManager.Config.ActiveWorkspace,
        }));
    }

    // CLOSE_WORKSPACE_TAB — close tab, switch if it was the active one
    private void HandleCloseTabAndMaybeSwitch(JsonElement? payload)
    {
        string? id = null;
        if (payload is not null && payload.Value.TryGetProperty("id", out var idEl))
            id = idEl.GetString() ?? "";
        var wasActive = _workspaceManager.Config.ActiveWorkspace == id;

        _workspaceManager.CloseTab(id ?? "");

        // if closed tab was active, we need to switch context
        if (wasActive)
        {
            _currentContext?.Dispose();
            _currentContext = null;

            var newActive = _workspaceManager.Config.ActiveWorkspace;
            if (newActive != null)
            {
                var ws = _workspaceManager.GetWorkspace(newActive);
                if (ws != null)
                    _currentContext = new WorkspaceContext(ws, _workspaceManager, _gitResolver, _junctionService);
            }
            BuildHandlers();
        }

        Send(IpcResponse.Success("CLOSE_WORKSPACE_TAB", new
        {
            openTabs = _workspaceManager.Config.OpenTabs,
            activeWorkspace = _workspaceManager.Config.ActiveWorkspace,
        }));
    }

    private void Send(IpcResponse response)
    {
        var json = response.ToJson();
        // PostWebMessageAsString must be called on the UI thread
        _uiContext.Post(_ => _webView.PostWebMessageAsString(json), null);
    }
}
