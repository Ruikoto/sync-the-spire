using SyncTheSpire.Adapters;

namespace SyncTheSpire.Services;

public class SaveMergeService
{
    private readonly JunctionService _junctionService;
    private readonly SaveBackupService _backupService;
    private readonly string[] _profileNames;
    private readonly string _moddedSubfolder;

    public SaveMergeService(JunctionService junctionService, SaveBackupService backupService, IGameAdapter adapter)
    {
        _junctionService = junctionService;
        _backupService = backupService;
        _profileNames = adapter.SaveProfileNames;
        _moddedSubfolder = adapter.ModdedSaveSubfolder;
    }

    // check junction status of all modded profiles
    public SaveMergeStatus GetStatus(string saveFolderPath)
    {
        var moddedDir = Path.Combine(saveFolderPath, _moddedSubfolder);
        var hasModded = !string.IsNullOrEmpty(_moddedSubfolder) && Directory.Exists(moddedDir);

        var profiles = _profileNames.Select(name =>
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

        var moddedDir = Path.Combine(saveFolderPath, _moddedSubfolder);

        foreach (var name in _profileNames)
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
