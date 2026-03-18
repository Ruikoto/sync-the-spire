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

    // compare normal vs modded profiles for the selection UI
    public List<ProfileComparison> CompareProfiles(string saveFolderPath)
    {
        var moddedDir = Path.Combine(saveFolderPath, "modded");

        return ProfileNames.Select(name =>
        {
            var normalPath = Path.Combine(saveFolderPath, name);
            var moddedPath = Path.Combine(moddedDir, name);

            var normal = Directory.Exists(normalPath)
                ? BuildProfileInfo(normalPath)
                : null;

            // only gather modded info if it's a real dir, not a junction
            var modded = Directory.Exists(moddedPath) && !_junctionService.IsJunction(moddedPath)
                ? BuildProfileInfo(moddedPath)
                : null;

            // recommend whichever was modified more recently
            var rec = "normal";
            if (normal != null && modded != null)
                rec = modded.LastModified > normal.LastModified ? "modded" : "normal";
            else if (modded != null && normal == null)
                rec = "modded";

            return new ProfileComparison(name, normal, modded, rec);
        }).ToList();
    }

    // execute merge: apply user choices then create junctions.
    // choices maps profileName -> "normal" | "modded", can be null if no comparison needed.
    public string Merge(string saveFolderPath, Dictionary<string, string>? choices)
    {
        var backupPath = _backupService.BackupSaveFolder(saveFolderPath);

        var moddedDir = Path.Combine(saveFolderPath, "modded");

        // apply choices: where user chose "modded", overwrite normal with modded data
        if (choices != null)
        {
            foreach (var (profileName, choice) in choices)
            {
                // reject keys that aren't valid profile names to prevent path traversal
                if (!ProfileNames.Contains(profileName)) continue;
                if (choice != "modded") continue;

                var normalPath = Path.Combine(saveFolderPath, profileName);
                var moddedPath = Path.Combine(moddedDir, profileName);

                if (!Directory.Exists(moddedPath) || _junctionService.IsJunction(moddedPath))
                    continue;

                if (Directory.Exists(normalPath))
                    Directory.Delete(normalPath, true);
                SaveBackupService.CopyDirectoryRecursive(moddedPath, normalPath);
            }
        }

        // wipe entire modded/ folder (junctions + real dirs)
        if (Directory.Exists(moddedDir))
        {
            // remove junctions first so we don't accidentally recurse into targets
            foreach (var sub in Directory.GetDirectories(moddedDir))
            {
                if (_junctionService.IsJunction(sub))
                    _junctionService.RemoveJunction(sub);
            }
            Directory.Delete(moddedDir, true);
        }

        // recreate modded/ and create junctions pointing to normal profiles
        Directory.CreateDirectory(moddedDir);
        foreach (var name in ProfileNames)
        {
            var normalPath = Path.Combine(saveFolderPath, name);
            if (!Directory.Exists(normalPath)) continue;

            var junctionPath = Path.Combine(moddedDir, name);
            _junctionService.CreateJunction(junctionPath, normalPath);
        }

        return backupPath;
    }

    // unlink: remove junctions, copy normal profiles into modded as real dirs
    public string Unlink(string saveFolderPath)
    {
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

    private static ProfileInfo BuildProfileInfo(string dirPath)
    {
        var files = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories);
        var size = files.Sum(f => new FileInfo(f).Length);
        var lastMod = files.Length > 0
            ? files.Max(f => File.GetLastWriteTime(f))
            : Directory.GetLastWriteTime(dirPath);
        return new ProfileInfo(size, lastMod, files.Length);
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

public record ProfileComparison(
    string Name,
    ProfileInfo? Normal,
    ProfileInfo? Modded,
    string Recommendation);

public record ProfileInfo(
    long SizeBytes,
    DateTime LastModified,
    int FileCount);
