using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class SteamFinderHandler : HandlerBase
{
    private readonly SteamFinderService _steamFinder;

    public SteamFinderHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        SteamFinderService steamFinder)
        : base(webView, uiContext)
    {
        _steamFinder = steamFinder;
    }

    public void HandleFindGamePath()
    {
        var result = _steamFinder.FindGamePath();
        if (result.Path is not null)
            Send(IpcResponse.Success("FIND_GAME_PATH", new { path = result.Path }));
        else
            Send(IpcResponse.Error("FIND_GAME_PATH", result.Error ?? "未找到游戏安装路径"));
    }

    public void HandleFindSavePath()
    {
        var result = _steamFinder.FindSaveAccounts();
        if (result.Accounts is not null)
        {
            Send(IpcResponse.Success("FIND_SAVE_PATH", new
            {
                basePath = result.BasePath,
                accounts = result.Accounts.Select(a => new
                {
                    steamId = a.SteamId64,
                    personaName = a.PersonaName,
                    mostRecent = a.MostRecent,
                    hasSave = a.HasSaveFolder
                })
            }));
        }
        else
        {
            Send(IpcResponse.Error("FIND_SAVE_PATH", result.Error ?? "未找到存档目录"));
        }
    }
}
