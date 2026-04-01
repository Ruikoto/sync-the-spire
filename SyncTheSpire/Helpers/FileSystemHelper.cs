namespace SyncTheSpire.Helpers;

public static class FileSystemHelper
{
    // walk up from startPath looking for a file/dir by name
    public static string? FindAncestorContaining(string startPath, string childName, bool isFile)
    {
        var dir = startPath;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, childName);
            if (isFile ? File.Exists(candidate) : Directory.Exists(candidate))
                return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    // walk up from startPath using a custom predicate
    public static string? FindAncestorContaining(string startPath, Func<string, bool> predicate)
    {
        var dir = startPath;
        while (!string.IsNullOrEmpty(dir))
        {
            if (predicate(dir))
                return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    /// <summary>
    /// git marks object files as read-only, so Directory.Delete chokes on Windows.
    /// strip the flag first, then nuke the whole tree.
    /// </summary>
    public static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attr = File.GetAttributes(file);
            if (attr.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, true);
    }
}
