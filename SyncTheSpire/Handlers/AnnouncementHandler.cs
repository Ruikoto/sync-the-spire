using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class AnnouncementHandler : HandlerBase
{
    private readonly ConfigService _configService;

    public AnnouncementHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService)
        : base(webView, uiContext)
    {
        _configService = configService;
    }

    public void HandleGetDismissedAnnouncements()
    {
        var ids = _configService.GetDismissedAnnouncements();
        Send(IpcResponse.Success("GET_DISMISSED_ANNOUNCEMENTS", new { ids }));
    }

    public void HandleDismissAnnouncement(JsonElement? payload)
    {
        if (payload is null || !payload.Value.TryGetProperty("id", out var idEl))
        {
            Send(IpcResponse.Success("DISMISS_ANNOUNCEMENT"));
            return;
        }
        var id = idEl.GetString();
        if (!string.IsNullOrEmpty(id))
            _configService.DismissAnnouncement(id);
        Send(IpcResponse.Success("DISMISS_ANNOUNCEMENT"));
    }
}
