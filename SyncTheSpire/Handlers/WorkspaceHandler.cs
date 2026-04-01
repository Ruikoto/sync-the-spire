using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Adapters;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

/// <summary>
/// Handles workspace CRUD + tab management IPC.
/// Lives outside the per-workspace lifecycle (doesn't need WorkspaceContext).
/// </summary>
public class WorkspaceHandler : HandlerBase
{
    private readonly WorkspaceManager _workspaceManager;

    public WorkspaceHandler(CoreWebView2 webView, SynchronizationContext uiContext, WorkspaceManager workspaceManager)
        : base(webView, uiContext)
    {
        _workspaceManager = workspaceManager;
    }

    // GET_WORKSPACES — return all workspaces + tab state
    public void HandleGetWorkspaces()
    {
        var workspaces = _workspaceManager.GetAllWorkspaces().Select(ws =>
        {
            var adapter = GameAdapterRegistry.Get(ws.GameType);
            return new
            {
                id = ws.Id,
                name = ws.Name,
                gameType = ws.GameType,
                gameDisplayName = adapter.DisplayName,
                isConfigured = ws.IsConfigured,
            };
        }).ToList();

        Send(IpcResponse.Success("GET_WORKSPACES", new
        {
            workspaces,
            openTabs = _workspaceManager.Config.OpenTabs,
            activeWorkspace = _workspaceManager.Config.ActiveWorkspace,
        }));
    }

    // GET_GAME_TYPES — available game adapters for workspace creation
    public void HandleGetGameTypes()
    {
        var types = GameAdapterRegistry.All.Select(a => new
        {
            typeKey = a.TypeKey,
            displayName = a.DisplayName,
        }).ToList();

        Send(IpcResponse.Success("GET_GAME_TYPES", types));
    }

    // CREATE_WORKSPACE — create + auto-open tab + set active
    public void HandleCreateWorkspace(JsonElement? payload)
    {
        var name = payload?.GetProperty("name").GetString() ?? "";
        var gameType = payload?.GetProperty("gameType").GetString() ?? "sts2";

        if (string.IsNullOrWhiteSpace(name))
        {
            Send(IpcResponse.Error("CREATE_WORKSPACE", "工作区名称不能为空"));
            return;
        }

        var ws = _workspaceManager.CreateWorkspace(name, gameType);
        _workspaceManager.SetActiveWorkspace(ws.Id);

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

    // DELETE_WORKSPACE — remove workspace + return updated state
    public void HandleDeleteWorkspace(JsonElement? payload)
    {
        var id = payload?.GetProperty("id").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(id))
        {
            Send(IpcResponse.Error("DELETE_WORKSPACE", "缺少工作区 ID"));
            return;
        }

        _workspaceManager.DeleteWorkspace(id);

        Send(IpcResponse.Success("DELETE_WORKSPACE", new
        {
            openTabs = _workspaceManager.Config.OpenTabs,
            activeWorkspace = _workspaceManager.Config.ActiveWorkspace,
        }));
    }

    // OPEN_WORKSPACE_TAB — add to open tabs (doesn't switch)
    public void HandleOpenTab(JsonElement? payload)
    {
        var id = payload?.GetProperty("id").GetString() ?? "";
        _workspaceManager.OpenTab(id);

        Send(IpcResponse.Success("OPEN_WORKSPACE_TAB", new
        {
            openTabs = _workspaceManager.Config.OpenTabs,
            activeWorkspace = _workspaceManager.Config.ActiveWorkspace,
        }));
    }

    // RENAME_WORKSPACE — update workspace name
    public void HandleRenameWorkspace(JsonElement? payload)
    {
        var id = payload?.GetProperty("id").GetString() ?? "";
        var newName = payload?.GetProperty("name").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(newName))
        {
            Send(IpcResponse.Error("RENAME_WORKSPACE", "名称不能为空"));
            return;
        }

        var ws = _workspaceManager.GetWorkspace(id);
        if (ws == null)
        {
            Send(IpcResponse.Error("RENAME_WORKSPACE", "工作区不存在"));
            return;
        }

        ws.Name = newName;
        _workspaceManager.UpdateWorkspace(ws);

        Send(IpcResponse.Success("RENAME_WORKSPACE", new { id, name = newName }));
    }
}
