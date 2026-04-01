using System.Reflection;
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
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var isNightly = version.StartsWith("nightly-");

        var types = GameAdapterRegistry.All.Select(a => new
        {
            typeKey = a.TypeKey,
            displayName = a.DisplayName,
            comingSoon = isNightly ? false : a.ComingSoon,
        }).ToList();

        Send(IpcResponse.Success("GET_GAME_TYPES", types));
    }

    // OPEN_WORKSPACE_TAB — add to open tabs (doesn't switch)
    public void HandleOpenTab(JsonElement? payload)
    {
        // M2 fix: use TryGetProperty + validate empty
        string? id = null;
        if (payload is not null && payload.Value.TryGetProperty("id", out var idEl))
            id = idEl.GetString();
        if (string.IsNullOrWhiteSpace(id))
        {
            Send(IpcResponse.Error("OPEN_WORKSPACE_TAB", "缺少工作区 ID"));
            return;
        }

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
        // M2 fix: use TryGetProperty
        string? id = null, newName = null;
        if (payload is not null)
        {
            if (payload.Value.TryGetProperty("id", out var idEl)) id = idEl.GetString();
            if (payload.Value.TryGetProperty("name", out var nameEl)) newName = nameEl.GetString();
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            Send(IpcResponse.Error("RENAME_WORKSPACE", "名称不能为空"));
            return;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            Send(IpcResponse.Error("RENAME_WORKSPACE", "工作区 ID 不能为空"));
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
