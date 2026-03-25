namespace SyncTheSpire.Services;

public class SaveMergeService
{
    private readonly JunctionService _junctionService;
    private readonly SaveBackupService _backupService;

    private static readonly string[] ProfileNames = ["profile1", "profile2", "profile3"];

    public SaveMergeService(JunctionService junctionService, SaveBackupService backupService)
    {
        _junctionService = junctionService;
        _backupService = backupService;
    }

    // check junction status of all 3 modded profiles
    public SaveMergeStatus GetStatus(string saveFolderPath)
    {
        var moddedDir = Path.Combine(saveFolderPath, "modded");
        var hasModded = Directory.Exists(moddedDir);

        var profiles = ProfileNames.Select(name =>
        {
            var normalPath = Path.Combine(saveFolderPath, name);
            var moddedPath = Path.Combine(moddedDir, name);
            var normalExists = Directory.Exists(normalPath);
            var moddedExists = Directory.Exists(moddedPath);
            var isJunction = moddedExists && _junctionService.IsJunction(moddedPath);
            return new ProfileJunctionInfo(name, normalExists, moddedExists, isJunction);
        }).ToList();

        var linkedCount = profiles.Count(p => p.IsJunction);
        var existingNormalCount = profiles.Count(p => p.NormalExists);

        return new SaveMergeStatus(
            HasModdedFolder: hasModded,
            // "fully linked" when at least one junction exists and all normal profiles are linked
            IsFullyLinked: hasModded && linkedCount > 0 && linkedCount == existingNormalCount,
            IsPartiallyLinked: hasModded && linkedCount > 0 && linkedCount < existingNormalCount,
            Profiles: profiles);
    }

    // unlink: remove junctions, copy normal profiles into modded as real dirs
    public string Unlink(string saveFolderPath)
    {
        LogService.Info($"Unlinking merged saves: {saveFolderPath}");
        var backupPath = _backupService.BackupSaveFolder(saveFolderPath);

        var moddedDir = Path.Combine(saveFolderPath, "modded");

        foreach (var name in ProfileNames)
        {
            var moddedPath = Path.Combine(moddedDir, name);
            var normalPath = Path.Combine(saveFolderPath, name);

            if (!Directory.Exists(moddedPath)) continue;

            if (_junctionService.IsJunction(moddedPath))
            {
                _junctionService.RemoveJunction(moddedPath);
                // copy normal data into modded location as real directory
                if (Directory.Exists(normalPath))
                    SaveBackupService.CopyDirectoryRecursive(normalPath, moddedPath);
            }
        }

        return backupPath;
    }
}

// ── DTOs ────────────────────────────────────────────────────────────

public record SaveMergeStatus(
    bool HasModdedFolder,
    bool IsFullyLinked,
    bool IsPartiallyLinked,
    List<ProfileJunctionInfo> Profiles);

public record ProfileJunctionInfo(
    string Name,
    bool NormalExists,
    bool ModdedExists,
    bool IsJunction);
