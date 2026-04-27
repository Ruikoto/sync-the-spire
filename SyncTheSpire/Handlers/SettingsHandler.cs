using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

/// <summary>
/// Global app settings (language, etc.) — not workspace-scoped.
/// </summary>
public class SettingsHandler : HandlerBase
{
    private readonly WorkspaceManager _workspaceManager;

    public SettingsHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        WorkspaceManager workspaceManager)
        : base(webView, uiContext)
    {
        _workspaceManager = workspaceManager;
    }

    public void HandleGetSettings()
    {
        var settings = _workspaceManager.Config.Settings;
        Send(IpcResponse.Success("GET_SETTINGS", new
        {
            language = settings.Language,
            theme = settings.Theme,
        }));
    }

    public void HandleSaveSettings(JsonElement? payload)
    {
        if (payload is null)
        {
            Send(IpcResponse.Error("SAVE_SETTINGS", "Missing payload"));
            return;
        }

        var settings = _workspaceManager.Config.Settings;

        if (payload.Value.TryGetProperty("language", out var langEl))
            settings.Language = langEl.GetString() ?? "zh-CN";

        if (payload.Value.TryGetProperty("theme", out var themeEl))
        {
            var t = themeEl.GetString();
            settings.Theme = t is "light" or "dark" or "system" ? t : "system";
        }

        _workspaceManager.SaveConfig();

        Send(IpcResponse.Success("SAVE_SETTINGS", new
        {
            language = settings.Language,
            theme = settings.Theme,
        }));
    }
}
