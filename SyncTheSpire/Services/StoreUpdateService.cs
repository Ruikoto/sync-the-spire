using Windows.Services.Store;
using WinRT.Interop;

namespace SyncTheSpire.Services;

/// <summary>
/// checks for and installs updates via Microsoft Store In-App Updates API.
/// only functional when running as MSIX-packaged app — all calls are no-ops otherwise.
/// </summary>
public class StoreUpdateService
{
    private StoreContext? _storeContext;
    private IntPtr _windowHandle;
    private IReadOnlyList<StorePackageUpdate>? _pendingUpdates;

    public void Initialize(IntPtr hwnd)
    {
        _windowHandle = hwnd;
    }

    private StoreContext GetContext()
    {
        if (_storeContext != null) return _storeContext;
        _storeContext = StoreContext.GetDefault();
        // desktop apps need window handle association for Store UI dialogs
        InitializeWithWindow.Initialize(_storeContext, _windowHandle);
        return _storeContext;
    }

    /// <summary>
    /// queries the Store for pending updates. caches result for subsequent install call.
    /// returns (hasUpdate, isMandatory). safe to call from any thread.
    /// </summary>
    public async Task<(bool HasUpdate, bool IsMandatory)> CheckForUpdatesAsync()
    {
        if (!DistributionHelper.IsMsixPackaged)
            return (false, false);

        try
        {
            var ctx = GetContext();
            var updates = await ctx.GetAppAndOptionalStorePackageUpdatesAsync();
            _pendingUpdates = updates;

            if (updates.Count == 0)
                return (false, false);

            var mandatory = updates.Any(u => u.Mandatory);
            return (true, mandatory);
        }
        catch (Exception ex)
        {
            // Store service unavailable, user not signed in, network down, etc.
            System.Diagnostics.Debug.WriteLine($"Store update check failed: {ex.Message}");
            _pendingUpdates = null;
            return (false, false);
        }
    }

    /// <summary>
    /// triggers Store download + install UI for cached pending updates.
    /// returns "completed", "canceled", "no_updates", or "error".
    /// </summary>
    public async Task<string> DownloadAndInstallAsync()
    {
        if (!DistributionHelper.IsMsixPackaged)
            return "error";

        // re-check if we don't have cached updates
        if (_pendingUpdates == null || _pendingUpdates.Count == 0)
        {
            var (hasUpdate, _) = await CheckForUpdatesAsync();
            if (!hasUpdate)
                return "no_updates";
        }

        try
        {
            var ctx = GetContext();
            var result = await ctx.RequestDownloadAndInstallStorePackageUpdatesAsync(_pendingUpdates!);

            return result.OverallState switch
            {
                StorePackageUpdateState.Completed => "completed",
                StorePackageUpdateState.Canceled => "canceled",
                _ => "error"
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Store update install failed: {ex.Message}");
            return "error";
        }
    }
}
