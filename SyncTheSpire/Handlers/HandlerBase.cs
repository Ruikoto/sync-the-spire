using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;

namespace SyncTheSpire.Handlers;

public abstract class HandlerBase
{
    protected readonly CoreWebView2 WebView;
    protected readonly SynchronizationContext UiContext;

    protected HandlerBase(CoreWebView2 webView, SynchronizationContext uiContext)
    {
        WebView = webView;
        UiContext = uiContext;
    }

    protected void Send(IpcResponse response)
    {
        var json = response.ToJson();
        // PostWebMessageAsString must be called on the UI thread
        UiContext.Post(_ => WebView.PostWebMessageAsString(json), null);
    }
}
