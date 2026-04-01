using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class StoreUpdateHandler : HandlerBase
{
    private readonly StoreUpdateService _storeUpdateService;

    public StoreUpdateHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        StoreUpdateService storeUpdateService)
        : base(webView, uiContext)
    {
        _storeUpdateService = storeUpdateService;
    }

    public async Task HandleCheckStoreUpdate()
    {
        try
        {
            var (hasUpdate, isMandatory) = await _storeUpdateService.CheckForUpdatesAsync();
            Send(IpcResponse.Success("CHECK_STORE_UPDATE", new { available = hasUpdate, mandatory = isMandatory }));
        }
        catch (Exception ex)
        {
            LogService.Error("CHECK_STORE_UPDATE failed", ex);
            Send(IpcResponse.Error("CHECK_STORE_UPDATE", ex.Message));
        }
    }

    public async Task HandleInstallStoreUpdate()
    {
        try
        {
            var result = await _storeUpdateService.DownloadAndInstallAsync();
            Send(IpcResponse.Success("INSTALL_STORE_UPDATE", new { result }));
        }
        catch (Exception ex)
        {
            LogService.Error("INSTALL_STORE_UPDATE failed", ex);
            Send(IpcResponse.Error("INSTALL_STORE_UPDATE", ex.Message));
        }
    }
}
