using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Adapters;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class SteamFinderHandler : HandlerBase
{
    private readonly IGameAdapter _adapter;

    public SteamFinderHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        IGameAdapter adapter)
        : base(webView, uiContext)
    {
        _adapter = adapter;
    }

    public void HandleFindGamePath()
    {
        if (!_adapter.SupportsAutoFind)
        {
            Send(IpcResponse.Error("FIND_GAME_PATH", "当前游戏类型不支持自动检测"));
            return;
        }

        var (path, error) = _adapter.FindGamePath();
        if (path is not null)
            Send(IpcResponse.Success("FIND_GAME_PATH", new { path }));
        else
            Send(IpcResponse.Error("FIND_GAME_PATH", error ?? "未找到游戏安装路径"));
    }

    public void HandleFindSavePath()
    {
        if (!_adapter.SupportsAutoFind)
        {
            Send(IpcResponse.Error("FIND_SAVE_PATH", "当前游戏类型不支持自动检测"));
            return;
        }

        var result = _adapter.FindSavePath();

        // multi-account result (Steam games)
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
        // single path result (simpler games)
        else if (result.SinglePath is not null)
        {
            Send(IpcResponse.Success("FIND_SAVE_PATH", new { path = result.SinglePath }));
        }
        else
        {
            Send(IpcResponse.Error("FIND_SAVE_PATH", result.Error ?? "未找到存档目录"));
        }
    }
}
