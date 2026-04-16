using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class SaveHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly SaveBackupService _backupService;
    private readonly SaveMergeService _mergeService;
    private readonly JunctionService _junctionService;

    public SaveHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        SaveBackupService backupService,
        SaveMergeService mergeService,
        JunctionService junctionService)
        : base(webView, uiContext)
    {
        _configService = configService;
        _backupService = backupService;
        _mergeService = mergeService;
        _junctionService = junctionService;
    }

    public void HandleGetSaveStatus()
    {
        var cfg = _configService.LoadConfig();

        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath) || !Directory.Exists(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Success("GET_SAVE_STATUS", new { isConfigured = false }));
            return;
        }

        var status = _mergeService.GetStatus(cfg.SaveFolderPath);

        var mergeState = status.IsFullyLinked ? "linked"
                       : status.IsPartiallyLinked ? "partial"
                       : status.HasModdedFolder ? "unlinked"
                       : "no_modded";

        Send(IpcResponse.Success("GET_SAVE_STATUS", new
        {
            isConfigured = true,
            mergeState,
            profiles = status.Profiles.Select(p => new
            {
                name = p.Name,
                normalExists = p.NormalExists,
                moddedExists = p.ModdedExists,
                isJunction = p.IsJunction
            })
        }));
    }

    public void HandleUnlinkSaves()
    {
        var cfg = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Error("UNLINK_SAVES", "存档路径未配置"));
            return;
        }

        Send(IpcResponse.Progress("UNLINK_SAVES", "正在取消合并..."));

        var backupPath = _mergeService.Unlink(cfg.SaveFolderPath);

        Send(IpcResponse.Success("UNLINK_SAVES", new
        {
            message = "存档已取消合并，Mod 存档恢复为独立副本。",
            backupName = Path.GetFileName(backupPath)
        }));
    }

    public void HandleBackupSaves()
    {
        var cfg = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Error("BACKUP_SAVES", "存档路径未配置"));
            return;
        }

        Send(IpcResponse.Progress("BACKUP_SAVES", "正在备份存档..."));

        var backupPath = _backupService.BackupSaveFolder(cfg.SaveFolderPath);

        Send(IpcResponse.Success("BACKUP_SAVES", new
        {
            message = $"存档已备份到 {Path.GetFileName(backupPath)}"
        }));
    }

    public void HandleGetBackupList()
    {
        var backups = _backupService.ListBackups();

        Send(IpcResponse.Success("GET_BACKUP_LIST", new
        {
            backups = backups.Select(b => new
            {
                name = b.Name,
                createdAt = new DateTimeOffset(b.CreatedAt).ToUnixTimeMilliseconds(),
                sizeBytes = b.SizeBytes,
                type = b.Type
            })
        }));
    }

    public void HandleRestoreBackup(JsonElement? payload)
    {
        // M2 fix: use TryGetProperty
        string? backupName = null;
        if (payload is not null && payload.Value.TryGetProperty("backupName", out var bnEl))
            backupName = bnEl.GetString();
        if (string.IsNullOrWhiteSpace(backupName))
        {
            Send(IpcResponse.Error("RESTORE_BACKUP", "未指定备份名称"));
            return;
        }

        // sanitize: prevent path traversal
        if (backupName.Contains("..") || backupName.Contains('/') || backupName.Contains('\\'))
        {
            Send(IpcResponse.Error("RESTORE_BACKUP", "备份名称无效"));
            return;
        }

        var cfg = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Error("RESTORE_BACKUP", "存档路径未配置"));
            return;
        }

        var backupPath = Path.Combine(_backupService.BackupDir, backupName);
        if (!Directory.Exists(backupPath))
        {
            Send(IpcResponse.Error("RESTORE_BACKUP", "备份不存在或已被删除"));
            return;
        }

        Send(IpcResponse.Progress("RESTORE_BACKUP", "正在备份当前存档并恢复..."));

        // auto-backup current state before restoring
        _backupService.BackupSaveFolder(cfg.SaveFolderPath);

        _backupService.RestoreSaveBackup(backupPath, cfg.SaveFolderPath, _junctionService);

        Send(IpcResponse.Success("RESTORE_BACKUP", new
        {
            message = "存档已恢复！恢复前的状态已自动备份。"
        }));
    }

    public void HandleDeleteBackup(JsonElement? payload)
    {
        // M2 fix: use TryGetProperty
        string? backupName = null;
        if (payload is not null && payload.Value.TryGetProperty("backupName", out var bnEl))
            backupName = bnEl.GetString();
        if (string.IsNullOrWhiteSpace(backupName))
        {
            Send(IpcResponse.Error("DELETE_BACKUP", "未指定备份名称"));
            return;
        }

        // M6 fix: same sanitization as RESTORE_BACKUP
        if (backupName.Contains("..") || backupName.Contains('/') || backupName.Contains('\\'))
        {
            Send(IpcResponse.Error("DELETE_BACKUP", "备份名称无效"));
            return;
        }

        _backupService.DeleteBackup(backupName);

        Send(IpcResponse.Success("DELETE_BACKUP", new
        {
            message = "备份已删除"
        }));
    }
}
