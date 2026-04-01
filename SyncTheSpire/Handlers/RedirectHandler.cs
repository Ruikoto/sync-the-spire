using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Adapters;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class RedirectHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly JunctionService _junctionService;
    private readonly IGameAdapter _adapter;

    public RedirectHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        JunctionService junctionService,
        IGameAdapter adapter)
        : base(webView, uiContext)
    {
        _configService = configService;
        _junctionService = junctionService;
        _adapter = adapter;
    }

    public void HandleGetRedirectStatus()
    {
        var cfg = _configService.LoadConfig();

        if (!_adapter.SupportsSaveRedirect)
        {
            Send(IpcResponse.Success("GET_REDIRECT_STATUS", new
            {
                isJunctionActive = false,
                isEnabled = false,
                supported = false
            }));
            return;
        }

        var isJunction = cfg.IsConfigured && _junctionService.IsJunction(cfg.GameModPath);

        if (!isJunction)
        {
            Send(IpcResponse.Success("GET_REDIRECT_STATUS", new
            {
                isJunctionActive = false,
                isEnabled = false,
                supported = true
            }));
            return;
        }

        var isEnabled = _adapter.IsSaveRedirectEnabled(cfg.GameModPath);

        Send(IpcResponse.Success("GET_REDIRECT_STATUS", new
        {
            isJunctionActive = true,
            isEnabled,
            supported = true
        }));
    }

    public void HandleSetRedirect(JsonElement? payload)
    {
        if (!_adapter.SupportsSaveRedirect)
        {
            Send(IpcResponse.Error("SET_REDIRECT", "当前游戏类型不支持存档重定向"));
            return;
        }

        var cfg = _configService.LoadConfig();
        if (!cfg.IsConfigured || !_junctionService.IsJunction(cfg.GameModPath))
        {
            Send(IpcResponse.Error("SET_REDIRECT", "Mod 未连接，请先连接 Mod"));
            return;
        }

        var enabled = payload?.GetProperty("enabled").GetBoolean() ?? true;

        if (enabled)
            _adapter.EnableSaveRedirect(cfg.GameModPath);
        else
            _adapter.DisableSaveRedirect(cfg.GameModPath);

        Send(IpcResponse.Success("SET_REDIRECT", new
        {
            isEnabled = enabled,
            message = enabled ? "存档重定向已启用" : "存档重定向已关闭"
        }));
    }
}
