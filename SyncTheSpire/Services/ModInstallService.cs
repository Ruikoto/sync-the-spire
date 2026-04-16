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

    /// <summary>
    /// extract mod(s) from an archive file into the working tree.
    /// returns list of installed mod names.
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

                var fileDest = Path.Combine(destPath, relativePath);
                var fileDir = Path.GetDirectoryName(fileDest);
                if (fileDir != null) Directory.CreateDirectory(fileDir);

                using var entryStream = entry.OpenEntryStream();
                using var fs = File.Create(fileDest);
                entryStream.CopyTo(fs);
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
