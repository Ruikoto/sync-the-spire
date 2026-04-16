using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Helpers;

/// <summary>
/// shared junction setup logic — used by multiple handlers
/// (ConfigHandler, GitBranchHandler, FilesystemHandler)
/// </summary>
public class JunctionHelper
{
    private readonly JunctionService _junctionService;
    private readonly SaveBackupService _backupService;
    private readonly Action<IpcResponse> _send;

    public JunctionHelper(JunctionService junctionService, SaveBackupService backupService, Action<IpcResponse> send)
    {
        _junctionService = junctionService;
        _backupService = backupService;
        _send = send;
    }

    public void EnsureJunction(string gameModPath, string repoPath)
    {
        if (_junctionService.IsJunction(gameModPath))
            return; // already good

        // backup existing real folder using the backup service
        if (Directory.Exists(gameModPath))
        {
            _backupService.BackupModFolder(gameModPath);
            Directory.Delete(gameModPath, true);
        }

        var ok = _junctionService.CreateJunction(gameModPath, repoPath);
        if (!ok)
        {
            // fallback: copy files instead
            _send(IpcResponse.Progress("JUNCTION_FALLBACK", "Junction 创建失败，降级为复制模式..."));
            _junctionService.FallbackCopy(repoPath, gameModPath);
        }
    }
}
