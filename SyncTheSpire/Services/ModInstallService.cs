using System.Text.Json;
using SharpCompress.Archives;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

/// <summary>
/// handles mod archive extraction with smart root detection.
/// supports ZIP, RAR, 7z via SharpCompress.
/// </summary>
public class ModInstallService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // soft cap on cumulative extracted size — defends against zip-bomb archives
    // (a few KB compressed expanding to GBs). 2 GiB is well above any legitimate mod.
    private const long MaxExtractedSize = 2L * 1024 * 1024 * 1024;

    private readonly ModScannerService _modScanner;

    public ModInstallService(ModScannerService modScanner)
    {
        _modScanner = modScanner;
    }

    /// <summary>
    /// extract mod(s) from an archive file into the working tree.
    /// returns list of installed mod names. if a mod with the same id already
    /// exists locally, its folder is removed first (newest wins).
    /// </summary>
    public List<string> InstallFromArchive(string archivePath, string workTreePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = ArchiveFactory.Open(stream);

        // first pass: find all manifest .json entries with valid mod id
        var manifests = new List<(ModInfo Mod, string ManifestKey, string ModRoot)>();

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;
            var key = NormalizePath(entry.Key);
            if (key == null || !key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var entryStream = entry.OpenEntryStream();
                using var reader = new StreamReader(entryStream);
                var json = reader.ReadToEnd();
                var mod = JsonSerializer.Deserialize<ModInfo>(json, JsonOpts);
                if (string.IsNullOrEmpty(mod?.Id)) continue;

                // mod root = directory containing the manifest
                var slashIdx = key.LastIndexOf('/');
                var modRoot = slashIdx >= 0 ? key[..(slashIdx + 1)] : "";

                // deduplicate by mod id
                if (manifests.All(m => !string.Equals(m.Mod.Id, mod.Id, StringComparison.OrdinalIgnoreCase)))
                    manifests.Add((mod, key, modRoot));
            }
            catch { /* not a valid manifest */ }
        }

        if (manifests.Count == 0)
            throw new InvalidOperationException("压缩包中未找到有效的 MOD 定义文件（需要包含 id 字段的 .json 文件）");

        var installed = new List<string>();
        long totalExtracted = 0;

        foreach (var (mod, manifestKey, modRoot) in manifests)
        {
            // determine destination folder name:
            // - if manifest is inside a directory, use that directory's LAST segment as folder name
            //   e.g. "MyMod/info.json" → folder "MyMod", "Parent/SubMod/info.json" → folder "SubMod"
            // - if manifest is at archive root, use the mod id
            string destFolderName;
            if (!string.IsNullOrEmpty(modRoot))
            {
                var trimmed = modRoot.TrimEnd('/');
                var lastSlash = trimmed.LastIndexOf('/');
                destFolderName = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
            }
            else
            {
                destFolderName = mod.Id!;
            }

            // overwrite any existing local mod with the same id — prevents two manifests
            // pointing at the same mod, which breaks multiplayer
            try { _modScanner.RemoveLocalModById(mod.Id!); }
            catch (Exception ex) { LogService.Warn($"Failed to remove existing mod {mod.Id} before install: {ex.Message}"); }

            var destPath = Path.Combine(workTreePath, destFolderName);
            Directory.CreateDirectory(destPath);

            // second pass: extract files belonging to this mod
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                var key = NormalizePath(entry.Key);
                if (key == null) continue;

                string relativePath;
                if (!string.IsNullOrEmpty(modRoot))
                {
                    // only extract files under this mod's root directory
                    if (!key.StartsWith(modRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    relativePath = key[modRoot.Length..];
                }
                else
                {
                    // manifest at archive root — only extract files also at root level
                    // (no slash in key = root file)
                    if (key.Contains('/')) continue;
                    relativePath = key;
                }

                if (string.IsNullOrEmpty(relativePath)) continue;

                var fileDest = Path.GetFullPath(Path.Combine(destPath, relativePath));
                // zip slip guard — refuse to write outside destPath
                if (!fileDest.StartsWith(Path.GetFullPath(destPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileDir = Path.GetDirectoryName(fileDest);
                if (fileDir != null) Directory.CreateDirectory(fileDir);

                // copy with running-total guard so a malicious archive can't fill the disk —
                // entry.Size is unreliable (uncompressed size from header may be spoofed),
                // so we count actual bytes written and abort partway through if we exceed the cap
                using var entryStream = entry.OpenEntryStream();
                using var fs = File.Create(fileDest);
                var buffer = new byte[81920];
                int read;
                while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalExtracted += read;
                    if (totalExtracted > MaxExtractedSize)
                        throw new InvalidOperationException(
                            $"压缩包解压后大小超过限制（{MaxExtractedSize / (1024 * 1024 * 1024)} GiB），可能是异常压缩包，已中止");
                    fs.Write(buffer, 0, read);
                }
            }

            installed.Add(mod.Name ?? mod.Id!);
        }

        return installed;
    }

    private static string? NormalizePath(string? key)
    {
        return key?.Replace('\\', '/');
    }
}
