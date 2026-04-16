namespace SyncTheSpire.Services;

public class SaveBackupService
{
    public string BackupDir { get; }

    public SaveBackupService(string backupDir)
    {
        BackupDir = backupDir;
    }

    // backup entire save folder, returns backup directory path
    public string BackupSaveFolder(string saveFolderPath)
    {
        var name = $"Save_backup_{DateTime.Now:yyyyMMdd_HHmmss}";
        var dest = Path.Combine(BackupDir, name);
        LogService.Info($"Backing up save folder: {name}");
        Directory.CreateDirectory(BackupDir);
        CopyDirectoryRecursive(saveFolderPath, dest);
        LogService.Info($"Save backup completed: {name}");
        return dest;
    }

    // backup game mod folder (replaces inline code in MessageRouter.EnsureJunction)
    public string BackupModFolder(string gameModPath)
    {
        var name = $"Mods_backup_{DateTime.Now:yyyyMMdd_HHmmss}";
        var dest = Path.Combine(BackupDir, name);
        LogService.Info($"Backing up mod folder: {name}");
        Directory.CreateDirectory(BackupDir);
        CopyDirectoryRecursive(gameModPath, dest);
        return dest;
    }

    // list all backups of both types, newest first
    public List<BackupEntry> ListBackups()
    {
        if (!Directory.Exists(BackupDir)) return [];

        return Directory.GetDirectories(BackupDir)
            .Select(dir =>
            {
                var dirName = Path.GetFileName(dir);
                var type = dirName.StartsWith("Save_backup_") ? "save"
                         : dirName.StartsWith("Mods_backup_") ? "mod"
                         : "unknown";
                return new BackupEntry(
                    dirName,
                    dir,
                    Directory.GetCreationTime(dir),
                    GetDirectorySize(dir),
                    type);
            })
            .Where(b => b.Type != "unknown")
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    // restore a save backup into the save folder.
    // caller is responsible for backing up current state first.
    public void RestoreSaveBackup(string backupPath, string saveFolderPath, JunctionService junctionService)
    {
        LogService.Info($"Restoring save backup: {Path.GetFileName(backupPath)}");
        // nuke any active junctions in modded/ before deleting
        var moddedDir = Path.Combine(saveFolderPath, "modded");
        if (Directory.Exists(moddedDir))
        {
            foreach (var sub in Directory.GetDirectories(moddedDir))
            {
                if (junctionService.IsJunction(sub))
                    junctionService.RemoveJunction(sub);
            }
        }

        // wipe current save folder contents (keep the folder itself)
        foreach (var f in Directory.GetFiles(saveFolderPath))
            File.Delete(f);
        foreach (var d in Directory.GetDirectories(saveFolderPath))
            Directory.Delete(d, true);

        // copy backup contents in
        CopyContentsInto(backupPath, saveFolderPath);
    }

    public void DeleteBackup(string backupName)
    {
        // sanitize: prevent path traversal
        if (backupName.Contains("..") || backupName.Contains('/') || backupName.Contains('\\'))
            return;

        LogService.Info($"Deleting backup: {backupName}");
        var path = Path.Combine(BackupDir, backupName);
        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }

    // ── helpers ──────────────────────────────────────────────────────

    public static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
        {
            // skip junctions/symlinks to avoid circular references
            var info = new DirectoryInfo(dir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    private static void CopyContentsInto(string source, string dest)
    {
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }
}

public record BackupEntry(
    string Name,
    string FullPath,
    DateTime CreatedAt,
    long SizeBytes,
    string Type);
