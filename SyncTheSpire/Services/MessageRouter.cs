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
    private GitBranchHandler? _gitBranchHandler;
    private SaveHandler? _saveHandler;
    private RedirectHandler _redirectHandler = null!;
    private AnnouncementHandler _announcementHandler = null!;
    private StoreUpdateHandler _storeUpdateHandler = null!;
    private SteamFinderHandler _steamFinderHandler = null!;
    private FilesystemHandler _filesystemHandler = null!;
    private ModManagerHandler? _modManagerHandler;

    // dispatch tables — rebuilt when workspace changes
    private Dictionary<string, Action<IpcRequest>> _fireAndForget = new();
    private Dictionary<string, Action<IpcRequest>> _gated = new();

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
        "GET_LOCAL_MODS_DETAILED", "DELETE_MOD", "INSTALL_MOD_FILES", "INSTALL_MOD_DROPPED",
        "GET_BRANCH_MODS_FOR_COPY", "COPY_MOD_FROM_BRANCH", "CLEAN_DUPLICATE_MANIFESTS",
        "PREFLIGHT_EXCLUDE_LARGE_FILES", "PREFLIGHT_CANCEL", "REBUILD_BRANCHES_ORPHAN",
        "RESET_UNPUSHED_COMMITS",
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
            _gitBranchHandler = new GitBranchHandler(_webView, _uiContext, ctx.ConfigService, ctx.GitService, ctx.NsfwDetection, ctx.ModScanner, _junctionService, junctionHelper, adapter, _workspaceManager);
            _saveHandler = new SaveHandler(_webView, _uiContext, ctx.ConfigService, ctx.BackupService, ctx.MergeService, _junctionService);
            _redirectHandler = new RedirectHandler(_webView, _uiContext, ctx.ConfigService, _junctionService, adapter);
            _announcementHandler = new AnnouncementHandler(_webView, _uiContext, ctx.ConfigService);
            _steamFinderHandler = new SteamFinderHandler(_webView, _uiContext, adapter);
            _filesystemHandler = new FilesystemHandler(_webView, _uiContext, ctx.ConfigService, _junctionService, ctx.BackupService, junctionHelper, adapter, _form);
            _modManagerHandler = new ModManagerHandler(_webView, _uiContext, ctx.ConfigService, ctx.ModScanner, new ModInstallService(ctx.ModScanner), _form);
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

            // pass null for GitService — ConfigHandler now accepts GitService? and null-checks all reads
            _configHandler = new ConfigHandler(_webView, _uiContext, stubCs, null, _junctionService, stubJH, stubAdapter, _workspaceManager);
            _gitBranchHandler = null;
            _saveHandler = null;
            _redirectHandler = new RedirectHandler(_webView, _uiContext, stubCs, _junctionService, stubAdapter);
            _announcementHandler = new AnnouncementHandler(_webView, _uiContext, stubCs);
            _steamFinderHandler = new SteamFinderHandler(_webView, _uiContext, stubAdapter);
            _filesystemHandler = new FilesystemHandler(_webView, _uiContext, stubCs, _junctionService, stubBackup, stubJH, stubAdapter, _form);
            _modManagerHandler = null;
        }

        BuildDispatchTables();
    }

    /// <summary>
    /// Populate dispatch tables based on current handler state.
    /// Fire-and-forget actions run without the gate; gated actions are serialized via SemaphoreSlim.
    /// </summary>
    private void BuildDispatchTables()
    {
        // ── fire-and-forget actions (no gate) ──────────────────────────────
        _fireAndForget = new Dictionary<string, Action<IpcRequest>>
        {
            // window chrome
            ["WINDOW_DRAG"] = _ => _uiContext.Post(_ => _form.BeginDrag(), null),
            ["WINDOW_RESIZE"] = req =>
            {
                var edge = req.Payload?.GetProperty("edge").GetString() ?? "";
                _uiContext.Post(_ => _form.BeginResize(edge), null);
            },
            ["WINDOW_MINIMIZE"] = _ => _uiContext.Post(_ => _form.WindowState = FormWindowState.Minimized, null),
            ["WINDOW_MAXIMIZE"] = _ => _uiContext.Post(_ =>
                _form.WindowState = _form.WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized, null),
            ["WINDOW_CLOSE"] = _ => _uiContext.Post(_ => _form.Close(), null),
            // PICK_FOLDER needs UI dialog — handle outside the gate to avoid blocking other IPC
            ["PICK_FOLDER"] = _ => _filesystemHandler.HandlePickFolder(),
            ["PICK_GAME_EXE"] = _ => _filesystemHandler.HandlePickGameExe(),
            // Store update actions use system UI / network calls — don't hold the gate
            ["CHECK_STORE_UPDATE"] = req => { _ = _storeUpdateHandler.HandleCheckStoreUpdate(); },
            ["INSTALL_STORE_UPDATE"] = req => { _ = _storeUpdateHandler.HandleInstallStoreUpdate(); },
            // steam auto-find — read-only filesystem/registry, no need for the gate
            ["FIND_GAME_PATH"] = _ => _steamFinderHandler.HandleFindGamePath(),
            ["FIND_SAVE_PATH"] = _ => _steamFinderHandler.HandleFindSavePath(),
            // workspace queries — read-only, no gate needed
            ["GET_WORKSPACES"] = _ => _workspaceHandler.HandleGetWorkspaces(),
            ["GET_GAME_TYPES"] = _ => _workspaceHandler.HandleGetGameTypes(),
        };

        // workspace-dependent fire-and-forget: branch mods read immutable git objects
        if (_gitBranchHandler is { } branchHandler)
        {
            _fireAndForget["GET_BRANCH_MODS"] = req => branchHandler.HandleGetBranchMods(req.Payload);
            _fireAndForget["GET_MOD_DIFF"] = _ => branchHandler.HandleGetModDiff();
        }
        else
        {
            _fireAndForget["GET_BRANCH_MODS"] = _ => Send(IpcResponse.Error("GET_BRANCH_MODS", "当前没有活跃的工作区"));
            _fireAndForget["GET_MOD_DIFF"] = _ => Send(IpcResponse.Error("GET_MOD_DIFF", "当前没有活跃的工作区"));
        }

        // mod archive picker — needs UI thread
        if (_modManagerHandler is { } modPicker)
            _fireAndForget["PICK_MOD_ARCHIVE"] = _ => modPicker.HandlePickModArchive();
        else
            _fireAndForget["PICK_MOD_ARCHIVE"] = _ => Send(IpcResponse.Error("PICK_MOD_ARCHIVE", "当前没有活跃的工作区"));

        // ── gated actions (serialized via SemaphoreSlim) ───────────────────
        _gated = new Dictionary<string, Action<IpcRequest>>
        {
            ["GET_STATUS"] = _ => _configHandler.HandleGetStatus(),
            // REFRESH_SYNC is handled separately in Route() with split-gate pattern
            ["GET_VERSION"] = _ => _configHandler.HandleGetVersion(),
            ["GET_CONFIG"] = _ => _configHandler.HandleGetConfig(),
            ["INIT_CONFIG"] = req => HandleInitConfigWithWorkspace(req.Payload),
            // junction / filesystem
            ["RESTORE_JUNCTION"] = _ => _filesystemHandler.HandleRestoreJunction(),
            ["OPEN_FOLDER"] = req => _filesystemHandler.HandleOpenFolder(req.Payload),
            ["LAUNCH_GAME"] = _ => _filesystemHandler.HandleLaunchGame(),
            ["SET_CUSTOM_EXE"] = req => _filesystemHandler.HandleSetCustomExe(req.Payload),
            // save redirect
            ["GET_REDIRECT_STATUS"] = _ => _redirectHandler.HandleGetRedirectStatus(),
            ["SET_REDIRECT"] = req => _redirectHandler.HandleSetRedirect(req.Payload),
            // announcements
            ["GET_DISMISSED_ANNOUNCEMENTS"] = _ => _announcementHandler.HandleGetDismissedAnnouncements(),
            ["DISMISS_ANNOUNCEMENT"] = req => _announcementHandler.HandleDismissAnnouncement(req.Payload),
            // workspace management
            ["CREATE_WORKSPACE"] = req => HandleCreateWorkspaceAndSwitch(req.Payload),
            ["DELETE_WORKSPACE"] = req => HandleDeleteWorkspaceAndSwitch(req.Payload),
            ["SWITCH_WORKSPACE"] = req => HandleSwitchWorkspace(req.Payload),
            ["OPEN_WORKSPACE_TAB"] = req => _workspaceHandler.HandleOpenTab(req.Payload),
            ["CLOSE_WORKSPACE_TAB"] = req => HandleCloseTabAndMaybeSwitch(req.Payload),
            ["RENAME_WORKSPACE"] = req => _workspaceHandler.HandleRenameWorkspace(req.Payload),
            // global settings
            ["GET_SETTINGS"] = _ => _settingsHandler.HandleGetSettings(),
            ["SAVE_SETTINGS"] = req => _settingsHandler.HandleSaveSettings(req.Payload),
        };

        // workspace-dependent gated actions (guarded by WorkspaceScopedActions check in Route)
        if (_gitBranchHandler is { } gb)
        {
            _gated["GET_BRANCHES"] = _ => gb.HandleGetBranches();
            _gated["SWITCH_TO_VANILLA"] = _ => gb.HandleSwitchToVanilla();
            _gated["SYNC_OTHER_BRANCH"] = req => gb.HandleSyncOtherBranch(req.Payload);
            _gated["CREATE_MY_BRANCH"] = req => gb.HandleCreateMyBranch(req.Payload);
            _gated["SAVE_AND_PUSH_MY_BRANCH"] = _ => gb.HandleSaveAndPush();
            _gated["FORCE_PUSH"] = _ => gb.HandleForcePush();
            _gated["RESET_TO_REMOTE"] = _ => gb.HandleResetToRemote();
            _gated["PREFLIGHT_EXCLUDE_LARGE_FILES"] = req => gb.HandlePreflightExcludeLargeFiles(req.Payload);
            _gated["PREFLIGHT_CANCEL"] = _ => Send(IpcResponse.Success("PREFLIGHT_CANCEL", new { }));
            _gated["REBUILD_BRANCHES_ORPHAN"] = req => gb.HandleRebuildBranchesOrphan(req.Payload);
            _gated["RESET_UNPUSHED_COMMITS"] = _ => gb.HandleResetUnpushedCommits();
        }

        if (_saveHandler is { } sh)
        {
            _gated["GET_SAVE_STATUS"] = _ => sh.HandleGetSaveStatus();
            _gated["UNLINK_SAVES"] = _ => sh.HandleUnlinkSaves();
            _gated["BACKUP_SAVES"] = _ => sh.HandleBackupSaves();
            _gated["GET_BACKUP_LIST"] = _ => sh.HandleGetBackupList();
            _gated["RESTORE_BACKUP"] = req => sh.HandleRestoreBackup(req.Payload);
            _gated["DELETE_BACKUP"] = req => sh.HandleDeleteBackup(req.Payload);
        }

        if (_modManagerHandler is { } mm)
        {
            _gated["GET_LOCAL_MODS_DETAILED"] = _ => mm.HandleGetLocalModsDetailed();
            _gated["DELETE_MOD"] = req => mm.HandleDeleteMod(req.Payload);
            _gated["INSTALL_MOD_FILES"] = req => mm.HandleInstallModFiles(req.Payload);
            _gated["GET_BRANCH_MODS_FOR_COPY"] = req => mm.HandleGetBranchModsForCopy(req.Payload);
            _gated["COPY_MOD_FROM_BRANCH"] = req => mm.HandleCopyModFromBranch(req.Payload);
            _gated["INSTALL_MOD_DROPPED"] = req => mm.HandleInstallModDropped(req.Payload);
            _gated["CLEAN_DUPLICATE_MANIFESTS"] = _ => mm.HandleCleanDuplicateManifests();
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
        // fire-and-forget — no serialization needed
        if (_fireAndForget.TryGetValue(req.Action, out var ff))
        {
            ff(req);
            return;
        }

        // REFRESH_SYNC: fetch outside the gate (slow network I/O),
        // then acquire gate briefly for local status reads only
        if (req.Action == "REFRESH_SYNC")
        {
            HandleRefreshSyncSplit();
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

            if (_gated.TryGetValue(req.Action, out var gated))
                gated(req);
            else
                Send(IpcResponse.Error(req.Action, $"Unknown action: {req.Action}"));
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
            Send(IpcResponse.Error(req.Action, FriendlyGitError(ex.Message)));
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("401") || ex.Message.Contains("403"))
        {
            LogService.Error($"[{req.Action}] Auth failure", ex);
            var authType = _currentContext?.ConfigService.Workspace.AuthType ?? "anonymous";
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
            Send(IpcResponse.Error(req.Action, FriendlyGitError(ex.Message)));
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

        // rebuild context — INIT_CONFIG may have changed GameInstallPath, which affects
        // WorkTreePath for non-junction adapters (generic). Without this, core.worktree
        // would still point to RepoPath instead of the user's sync folder.
        RebuildCurrentContext();
    }

    /// <summary>
    /// recreate WorkspaceContext from the current config, recomputing WorkTreePath
    /// and fixing core.worktree in git config if the repo exists.
    /// </summary>
    private void RebuildCurrentContext()
    {
        var ws = _currentContext!.Config;
        _currentContext.Dispose();
        _currentContext = new WorkspaceContext(ws, _workspaceManager, _gitResolver, _junctionService);
        BuildHandlers();

        // fix core.worktree to match the recomputed WorkTreePath
        _currentContext.GitService.UpdateWorkTree();
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

    /// <summary>
    /// REFRESH_SYNC with split-gate: fetch runs outside the gate so it doesn't block
    /// other IPC operations during slow network I/O, then briefly acquires gate for local reads.
    /// </summary>
    private void HandleRefreshSyncSplit()
    {
        var ctx = _currentContext;
        if (ctx == null)
        {
            Send(IpcResponse.Error("REFRESH_SYNC", "请先选择一个工作区"));
            return;
        }

        // fetch OUTSIDE the gate — this is the slow network part
        try
        {
            var ws = ctx.ConfigService.Workspace;
            if (ws.IsConfigured && ctx.GitService is { IsRepoValid: true, IsOnInitBranch: false })
            {
                // compact progress for refresh — just show percent, detail in tooltip
                ctx.GitService.OnTransferProgress = p =>
                    Send(IpcResponse.Progress("REFRESH_SYNC", $"{p.Percent}%", p.Percent, p.Detail));
                try
                {
                    ctx.GitService.Fetch();
                }
                finally
                {
                    ctx.GitService.OnTransferProgress = null;
                }
            }
        }
        catch (Exception ex)
        {
            // fetch failure is non-fatal — we'll still report local state
            LogService.Warn($"[REFRESH_SYNC] fetch failed (non-blocking): {ex.Message}");
        }

        // acquire gate for quick local reads (branch, junction, ahead/behind)
        _gate.Wait();
        try
        {
            // workspace may have changed while we were fetching — verify
            if (_currentContext != ctx)
            {
                LogService.Info("[REFRESH_SYNC] workspace changed during fetch, discarding stale result");
                return;
            }

            _configHandler.HandleRefreshSyncLocal();
        }
        catch (Exception ex)
        {
            LogService.Error("[REFRESH_SYNC] error reading local status", ex);
            Send(IpcResponse.Error("REFRESH_SYNC", $"刷新失败：{ex.Message}"));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Send(IpcResponse response)
    {
        var json = response.ToJson();
        // PostWebMessageAsString must be called on the UI thread
        _uiContext.Post(_ => _webView.PostWebMessageAsString(json), null);
    }

    /// <summary>
    /// translate raw git error messages into user-friendly Chinese text.
    /// special-cases GitHub connectivity issues with domestic platform suggestions.
    /// </summary>
    private static string FriendlyGitError(string rawMessage)
    {
        var msg = rawMessage;

        // connection failures
        if (msg.Contains("unable to access") || msg.Contains("Could not resolve host") ||
            msg.Contains("Connection refused") || msg.Contains("Connection timed out") ||
            msg.Contains("Failed to connect") || msg.Contains("Timed out"))
        {
            var friendly = "无法连接到远程仓库，请检查网络连接和仓库地址是否正确。";

            // GitHub-specific hint for users in China
            if (msg.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                friendly += "\n\n国内部分地区难以访问 GitHub，建议使用 AtomGit、Gitee 等国内 Git 平台。";

            return friendly;
        }

        // SSL/TLS errors
        if (msg.Contains("SSL") || msg.Contains("certificate"))
            return "SSL 连接失败，可能是网络环境或代理设置导致。请检查网络连接。";

        // repo not found
        if (msg.Contains("not found") || msg.Contains("404") ||
            msg.Contains("does not appear to be a git repository"))
            return "仓库未找到，请检查仓库地址是否正确。";

        // timeout
        if (msg.Contains("timed out"))
            return "操作超时，请检查网络连接后重试。";

        // repo total size / quota (GitHub: "Repository size limit exceeded";
        // Gitee: "repository over quota" / "超过仓库总大小"; GitLab: data quota)
        if (msg.Contains("repository size limit", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("over quota", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("data quota", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("超过") && msg.Contains("总大小"))
            return "云端仓库已满（仓库总大小或配额已超限）。请使用「清理分支历史」清除已提交的历史大文件，或迁移到容量更大的平台。";

        // LFS-specific (over quota or single-object exceed) — message is prefixed "LFS:"
        if (msg.Contains("LFS:", StringComparison.OrdinalIgnoreCase) &&
            (msg.Contains("exceed", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("quota",  StringComparison.OrdinalIgnoreCase)))
            return "云端 LFS 配额已超限或单文件超出 LFS 限制。本工具不再提供 LFS 写支持，建议使用「清理分支历史」处理。";

        // generic single-file size rejection
        if (msg.Contains("File size exceeds") ||
            msg.Contains("exceeds the maximum") ||
            msg.Contains("larger than") ||
            msg.Contains("HTTP 413") ||
            (msg.Contains("pre-receive hook declined") && msg.Contains("size")))
            return "有文件超过了云端平台的单文件上传限制。可以在提交前排除这些文件，或使用「清理分支历史」清除已提交的历史大文件。";

        // shallow update error — usually from force-pushing orphan to a shallow clone
        if (msg.Contains("shallow update not allowed"))
            return "推送失败：远端不接受此次历史重写操作，可能是仓库为浅克隆状态。请尝试先重新初始化仓库。";

        // fallback: strip the internal prefix like "git clone failed: " and show a cleaner message
        var colonIdx = msg.IndexOf("failed:");
        if (colonIdx >= 0)
        {
            var detail = msg[(colonIdx + 7)..].Trim();
            // strip "Cloning into '...'..." prefix from clone errors
            if (detail.StartsWith("Cloning into"))
            {
                var fatalIdx = detail.IndexOf("fatal:");
                if (fatalIdx >= 0)
                    detail = detail[(fatalIdx + 6)..].Trim();
            }
            return $"Git 操作失败：{detail}";
        }

        return $"Git 操作失败：{msg}";
    }
}
