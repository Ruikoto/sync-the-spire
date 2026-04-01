using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Helpers;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class FilesystemHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly JunctionService _junctionService;
    private readonly SaveBackupService _backupService;
    private readonly JunctionHelper _junctionHelper;
    private readonly MainForm _form;

    public FilesystemHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        JunctionService junctionService,
        SaveBackupService backupService,
        JunctionHelper junctionHelper,
        MainForm form)
        : base(webView, uiContext)
    {
        _configService = configService;
        _junctionService = junctionService;
        _backupService = backupService;
        _junctionHelper = junctionHelper;
        _form = form;
    }

    public void HandleOpenFolder(JsonElement? payload)
    {
        var folderType = payload?.GetProperty("folderType").GetString();
        var cfg = _configService.LoadConfig();

        var path = folderType switch
        {
            "game" => cfg.GameInstallPath,
            "mod" => cfg.GameModPath,
            "save" => cfg.SaveFolderPath,
            "config" => ConfigService.AppDataDirPath,
            "backup" => _backupService.BackupDir,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Send(IpcResponse.Error("OPEN_FOLDER", "文件夹路径不存在或未配置"));
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = false
        });

        Send(IpcResponse.Success("OPEN_FOLDER"));
    }

    /// <summary>
    /// open native folder browser dialog, return selected path
    /// </summary>
    public void HandlePickFolder()
    {
        string? selectedPath = null;

        // FolderBrowserDialog must run on STA thread
        var tcs = new TaskCompletionSource<string?>();
        UiContext.Post(_ =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择文件夹",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog(_form) == DialogResult.OK)
                selectedPath = dialog.SelectedPath;

            tcs.SetResult(selectedPath);
        }, null);

        var result = tcs.Task.GetAwaiter().GetResult();

        if (!string.IsNullOrWhiteSpace(result))
            Send(IpcResponse.Success("PICK_FOLDER", new { path = result }));
        else
            Send(IpcResponse.Success("PICK_FOLDER", new { path = (string?)null }));
    }

    public void HandleRestoreJunction()
    {
        var cfg = _configService.LoadConfig();
        _junctionHelper.EnsureJunction(cfg.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("RESTORE_JUNCTION", new { message = "Mod 文件夹已恢复连接。" }));
    }
}
