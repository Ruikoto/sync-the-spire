using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Adapters;
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
    private readonly IGameAdapter _adapter;
    private readonly MainForm _form;

    public FilesystemHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        JunctionService junctionService,
        SaveBackupService backupService,
        JunctionHelper junctionHelper,
        IGameAdapter adapter,
        MainForm form)
        : base(webView, uiContext)
    {
        _configService = configService;
        _junctionService = junctionService;
        _backupService = backupService;
        _junctionHelper = junctionHelper;
        _adapter = adapter;
        _form = form;
    }

    public void HandleOpenFolder(JsonElement? payload)
    {
        // M2 fix: use TryGetProperty
        string? folderType = null;
        if (payload is not null && payload.Value.TryGetProperty("folderType", out var ftEl))
            folderType = ftEl.GetString();

        var ws = _configService.Workspace;

        var path = folderType switch
        {
            "game" => ws.GameInstallPath,
            "mod" => ws.GameModPath,
            "save" => ws.SaveFolderPath,
            "config" => ConfigService.AppDataDirPath,
            "backup" => _backupService.BackupDir,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Send(IpcResponse.Error("OPEN_FOLDER", "文件夹路径不存在或未配置"));
            return;
        }

        // use ShellExecute so the OS respects the user's default file manager
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
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
        if (_adapter.SupportsJunction)
            _junctionHelper.EnsureJunction(_configService.Workspace.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("RESTORE_JUNCTION", new { message = "Mod 文件夹已恢复连接。" }));
    }

    /// <summary>
    /// launch the game via custom exe or steam:// URL scheme
    /// </summary>
    public void HandleLaunchGame()
    {
        var customExe = _configService.Workspace.CustomExePath;
        LogService.Info($"LaunchGame: customExe='{customExe}'");

        if (!string.IsNullOrWhiteSpace(customExe))
        {
            if (!File.Exists(customExe))
            {
                Send(IpcResponse.Error("LAUNCH_GAME", $"自定义路径不存在：{customExe}"));
                return;
            }
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = customExe,
                UseShellExecute = true
            });
            Send(IpcResponse.Success("LAUNCH_GAME"));
            return;
        }

        if (_adapter.SteamAppId is { } appId)
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{appId}",
                UseShellExecute = true
            });
            Send(IpcResponse.Success("LAUNCH_GAME"));
            return;
        }

        Send(IpcResponse.Error("LAUNCH_GAME", "未配置自定义启动路径且无 Steam 支持"));
    }

    /// <summary>
    /// open native file picker for exe selection
    /// </summary>
    public void HandlePickGameExe()
    {
        string? selectedPath = null;
        var tcs = new TaskCompletionSource<string?>();
        UiContext.Post(_ =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择游戏可执行文件",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(_form) == DialogResult.OK)
                selectedPath = dialog.FileName;
            tcs.SetResult(selectedPath);
        }, null);

        var result = tcs.Task.GetAwaiter().GetResult();
        LogService.Info($"PICK_GAME_EXE result: '{result}'");
        Send(IpcResponse.Success("PICK_GAME_EXE", new { path = result ?? "" }));
    }

    /// <summary>
    /// save or clear custom exe path for game launch
    /// </summary>
    public void HandleSetCustomExe(JsonElement? payload)
    {
        string path = string.Empty;
        if (payload is not null && payload.Value.TryGetProperty("path", out var pathEl))
            path = pathEl.GetString() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            Send(IpcResponse.Error("SET_CUSTOM_EXE", $"文件不存在：{path}"));
            return;
        }

        _configService.Workspace.CustomExePath = path;
        _configService.SaveWorkspace();
        Send(IpcResponse.Success("SET_CUSTOM_EXE", new { customExePath = path }));
    }
}
