using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SyncTheSpire.Services;

public class JunctionService
{
    /// <summary>
    /// create an NTFS directory junction (reparse point).
    /// junctionPath = the "shortcut" that appears in the game folder.
    /// targetPath   = the real repo directory in AppData.
    /// </summary>
    public bool CreateJunction(string junctionPath, string targetPath)
    {
        // if something already exists at junctionPath, bail out
        if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
            throw new InvalidOperationException($"Path already exists: {junctionPath}");

        try
        {
            // use cmd /c mklink /J -- works without elevation on NTFS
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi)!;
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// remove junction without deleting the real target files.
    /// Directory.Delete on a junction just removes the reparse point.
    /// </summary>
    public void RemoveJunction(string junctionPath)
    {
        if (!Directory.Exists(junctionPath)) return;

        if (IsJunction(junctionPath))
        {
            // this only removes the reparse point, not the target
            Directory.Delete(junctionPath, false);
        }
    }

    public bool IsJunction(string path)
    {
        if (!Directory.Exists(path)) return false;
        var info = new DirectoryInfo(path);
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>
    /// fallback when junction creation fails: physically copy files from repo to game dir
    /// </summary>
    public void FallbackCopy(string sourceDir, string destDir)
    {
        if (!Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            // skip .git internals
            var relativePath = Path.GetRelativePath(sourceDir, file);
            if (relativePath.StartsWith(".git"))
                continue;

            var destFile = Path.Combine(destDir, relativePath);
            var destFileDir = Path.GetDirectoryName(destFile)!;
            if (!Directory.Exists(destFileDir))
                Directory.CreateDirectory(destFileDir);

            File.Copy(file, destFile, overwrite: true);
        }
    }
}
