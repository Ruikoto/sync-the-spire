using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class RedirectHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly JunctionService _junctionService;

    private const string RedirectModId = "ModProfileBypass";
    // only these files belong to the redirect mod — delete precisely, nothing else
    private static readonly string[] RedirectModFiles = ["ModProfileBypass.dll", "ModProfileBypass.json"];

    public RedirectHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        JunctionService junctionService)
        : base(webView, uiContext)
    {
        _configService = configService;
        _junctionService = junctionService;
    }

    public void HandleGetRedirectStatus()
    {
        var cfg = _configService.LoadConfig();
        var isJunction = cfg.IsConfigured && _junctionService.IsJunction(cfg.GameModPath);

        if (!isJunction)
        {
            Send(IpcResponse.Success("GET_REDIRECT_STATUS", new
            {
                isJunctionActive = false,
                isEnabled = false
            }));
            return;
        }

        // mod is "enabled" when both files exist in the game mods dir
        var modDir = Path.Combine(cfg.GameModPath, RedirectModId);
        var isEnabled = RedirectModFiles.All(f => File.Exists(Path.Combine(modDir, f)));

        Send(IpcResponse.Success("GET_REDIRECT_STATUS", new
        {
            isJunctionActive = true,
            isEnabled
        }));
    }

    public void HandleSetRedirect(JsonElement? payload)
    {
        var cfg = _configService.LoadConfig();
        if (!cfg.IsConfigured || !_junctionService.IsJunction(cfg.GameModPath))
        {
            Send(IpcResponse.Error("SET_REDIRECT", "Mod 未连接，请先连接 Mod"));
            return;
        }

        var enabled = payload?.GetProperty("enabled").GetBoolean() ?? true;
        var modDir = Path.Combine(cfg.GameModPath, RedirectModId);

        if (enabled)
        {
            // deploy: copy mod files from bundled assets
            var assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", RedirectModId);
            if (!Directory.Exists(assetsDir))
            {
                Send(IpcResponse.Error("SET_REDIRECT", "重定向 Mod 资源缺失，请重新安装软件"));
                return;
            }
            Directory.CreateDirectory(modDir);
            foreach (var file in RedirectModFiles)
                File.Copy(Path.Combine(assetsDir, file), Path.Combine(modDir, file), overwrite: true);
        }
        else
        {
            // remove: delete only the exact mod files, leave the folder if other stuff exists
            foreach (var file in RedirectModFiles)
            {
                var path = Path.Combine(modDir, file);
                if (File.Exists(path))
                    File.Delete(path);
            }

            // clean up empty folder
            if (Directory.Exists(modDir) && !Directory.EnumerateFileSystemEntries(modDir).Any())
                Directory.Delete(modDir);
        }

        Send(IpcResponse.Success("SET_REDIRECT", new
        {
            isEnabled = enabled,
            message = enabled ? "存档重定向已启用" : "存档重定向已关闭"
        }));
    }
}
