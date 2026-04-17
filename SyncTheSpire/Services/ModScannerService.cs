using System.Text.Json;
using LibGit2Sharp;
using SyncTheSpire.Helpers;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

/// <summary>
/// Handles all mod scanning, dependency analysis, and mod file operations.
/// Extracted from GitService to keep git ops and mod logic separate.
/// </summary>
public class ModScannerService
{
    private readonly ConfigService _config;

    private static readonly JsonSerializerOptions ModJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ModScannerService(ConfigService config)
    {
        _config = config;
    }

    private string WorkTreePath => _config.WorkTreePath;
    private string GitDirPath => _config.GitDirPath;

    private Repository OpenRepo() => new(GitDirPath);

    // ── local working tree scan ─────────────────────────────────────────

    /// <summary>
    /// scan the local working tree for mod definitions (filesystem equivalent of GetBranchMods).
    /// walks all .json files under WorkTreePath and keeps entries with a valid "id" field.
    /// </summary>
    public List<ModInfo> GetLocalMods()
    {
        return ScanLocalModManifests(WorkTreePath);
    }

    /// <summary>
    /// shared helper: recursively walk a directory for .json mod manifests.
    /// returns one ModInfo per valid manifest found.
    /// </summary>
    private static List<ModInfo> ScanLocalModManifests(string rootPath)
    {
        var mods = new List<ModInfo>();
        if (!Directory.Exists(rootPath)) return mods;

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(file);
                var mod = JsonSerializer.Deserialize<ModInfo>(json, ModJsonOpts);
                if (!string.IsNullOrEmpty(mod?.Id))
                    mods.Add(mod);
            }
            catch
            {
                // not a mod definition or malformed json, skip
            }
        }
        return mods;
    }

    // ── remote branch tree scan ─────────────────────────────────────────

    /// <summary>
    /// read all mod definitions from a remote branch's file tree without checkout.
    /// walks the commit tree recursively, tries to deserialize every .json blob,
    /// and keeps entries that have a valid "id" field.
    /// </summary>
    public List<ModInfo> GetBranchMods(string branchName)
    {
        using var repo = OpenRepo();
        var branch = repo.Branches[$"origin/{branchName}"];
        if (branch is null) return [];

        var mods = new List<ModInfo>();
        ScanTreeForMods(branch.Tip.Tree, mods);
        return mods;
    }

    private static void ScanTreeForMods(Tree tree, List<ModInfo> mods)
    {
        foreach (var entry in tree)
        {
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                ScanTreeForMods((Tree)entry.Target, mods);
            }
            else if (entry.TargetType == TreeEntryTargetType.Blob &&
                     entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var blob = (Blob)entry.Target;
                    var mod = JsonSerializer.Deserialize<ModInfo>(blob.GetContentText(), ModJsonOpts);
                    if (!string.IsNullOrEmpty(mod?.Id))
                        mods.Add(mod);
                }
                catch
                {
                    // not a mod definition or malformed json, skip
                }
            }
        }
    }

    // ── detailed local scan + dependency analysis ────────────────────────

    /// <summary>
    /// deep scan of local working tree — enriched ModInfo with folder, file list, size, integrity.
    /// reuses the same recursive .json scan as GetLocalMods, then augments each mod
    /// with its containing folder info, file list, and integrity checks.
    /// </summary>
    public List<ModInfo> GetLocalModsDetailed()
    {
        if (!Directory.Exists(WorkTreePath)) return [];

        // id -> (manifest, jsonFilePath) — deduplicate by id, first match wins
        var found = new Dictionary<string, (ModInfo Mod, string JsonPath)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(WorkTreePath, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(file);
                var mod = JsonSerializer.Deserialize<ModInfo>(json, ModJsonOpts);
                if (!string.IsNullOrEmpty(mod?.Id) && !found.ContainsKey(mod.Id))
                    found[mod.Id] = (mod, file);
            }
            catch { /* not a valid manifest, skip */ }
        }

        var result = new List<ModInfo>();
        foreach (var (mod, jsonPath) in found.Values)
        {
            // the mod's "folder" is the directory containing its manifest json
            var modDir = Path.GetDirectoryName(jsonPath)!;
            var folderName = Path.GetRelativePath(WorkTreePath, modDir).Replace('\\', '/');
            // if manifest is at the WorkTreePath root itself, use the file name as identifier
            if (folderName == ".") folderName = Path.GetFileNameWithoutExtension(jsonPath);

            // collect all files relative to the mod folder
            var files = new List<string>();
            long totalSize = 0;
            foreach (var f in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories))
            {
                files.Add(Path.GetRelativePath(modDir, f));
                try { totalSize += new FileInfo(f).Length; } catch { /* access denied etc */ }
            }

            // integrity check
            var missingFiles = new List<string>();
            if (mod.HasDll && !files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                missingFiles.Add(".dll");
            if (mod.HasPck && !files.Any(f => f.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)))
                missingFiles.Add(".pck");

            result.Add(mod with
            {
                FolderName = folderName,
                FolderPath = modDir,
                Files = files,
                SizeBytes = totalSize,
                MissingFiles = missingFiles,
            });
        }

        return result;
    }

    /// <summary>
    /// cross-reference dependencies across all mods and produce ghost entries for missing ones.
    /// enriches each mod's DependedBy list and returns (realMods, ghostMods).
    /// </summary>
    public static (List<ModInfo> Mods, List<ModInfo> Ghosts) AnalyzeDependencies(List<ModInfo> allMods)
    {
        var byId = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in allMods)
            if (!string.IsNullOrEmpty(m.Id))
                byId.TryAdd(m.Id, m);

        // temporary lookup for building DependedBy lists
        var dependedByMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var ghostIds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in allMods)
        {
            foreach (var depId in mod.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(depId)) continue;

                if (byId.ContainsKey(depId))
                {
                    if (!dependedByMap.ContainsKey(depId))
                        dependedByMap[depId] = [];
                    dependedByMap[depId].Add(mod.Id!);
                }
                else
                {
                    // missing dependency -> ghost
                    if (!ghostIds.ContainsKey(depId))
                        ghostIds[depId] = [];
                    ghostIds[depId].Add(mod.Id!);
                }
            }
        }

        // enrich real mods with DependedBy
        var enriched = allMods.Select(m =>
        {
            var db = dependedByMap.GetValueOrDefault(m.Id ?? "", []);
            return db.Count > 0 ? m with { DependedBy = db } : m;
        }).ToList();

        // create ghost mod entries for missing dependencies
        var ghosts = ghostIds.Select(kv => new ModInfo
        {
            Id = kv.Key,
            Name = kv.Key,
            DependedBy = kv.Value,
        }).ToList();

        return (enriched, ghosts);
    }

    // ── branch mod copy ─────────────────────────────────────────────────

    /// <summary>
    /// read mods from a remote branch with folder name info (for branch copy feature).
    /// recursively scans all subtrees for manifest .json files, same approach as local scan.
    /// each mod's FolderName = the top-level directory it belongs to.
    /// </summary>
    public List<ModInfo> GetBranchModsForCopy(string branchName)
    {
        using var repo = OpenRepo();
        var branch = repo.Branches[$"origin/{branchName}"];
        if (branch is null) return [];

        var tree = branch.Tip.Tree;
        // id -> (mod, topLevelFolder) — deduplicate by id, first match wins
        var found = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in tree)
        {
            if (entry.TargetType != TreeEntryTargetType.Tree) continue;
            var topFolder = entry.Name;
            // recursively scan this subtree for manifest jsons
            ScanTreeForManifests((Tree)entry.Target, topFolder, found);
        }

        return found.Values.ToList();
    }

    /// <summary>
    /// recursively walk a git tree looking for .json files that are valid mod manifests.
    /// each discovered mod gets FolderName set to topLevelFolder (the root dir it lives under).
    /// </summary>
    private void ScanTreeForManifests(Tree tree, string topLevelFolder, Dictionary<string, ModInfo> found)
    {
        foreach (var entry in tree)
        {
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                ScanTreeForManifests((Tree)entry.Target, topLevelFolder, found);
            }
            else if (entry.TargetType == TreeEntryTargetType.Blob
                     && entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var blob = (Blob)entry.Target;
                    var mod = JsonSerializer.Deserialize<ModInfo>(blob.GetContentText(), ModJsonOpts);
                    if (!string.IsNullOrEmpty(mod?.Id) && !found.ContainsKey(mod.Id))
                        found[mod.Id] = mod with { FolderName = topLevelFolder };
                }
                catch { /* not a valid manifest */ }
            }
        }
    }

    /// <summary>
    /// extract a mod folder from a remote branch's git tree into local working tree.
    /// reads blobs from git objects — no checkout needed.
    /// </summary>
    public void CopyModFromBranch(string branchName, string modFolderName)
    {
        using var repo = OpenRepo();
        var branch = repo.Branches[$"origin/{branchName}"];
        if (branch is null)
            throw new InvalidOperationException($"分支不存在：{branchName}");

        var rootTree = branch.Tip.Tree;
        var modEntry = rootTree[modFolderName];
        if (modEntry is null || modEntry.TargetType != TreeEntryTargetType.Tree)
            throw new InvalidOperationException($"分支 {branchName} 中未找到 MOD 文件夹：{modFolderName}");

        var destDir = Path.Combine(WorkTreePath, modFolderName);
        ExtractTreeToDir((Tree)modEntry.Target, destDir);
    }

    private static void ExtractTreeToDir(Tree tree, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var entry in tree)
        {
            var targetPath = Path.Combine(destDir, entry.Name);
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                ExtractTreeToDir((Tree)entry.Target, targetPath);
            }
            else if (entry.TargetType == TreeEntryTargetType.Blob)
            {
                var blob = (Blob)entry.Target;
                using var stream = blob.GetContentStream();
                using var fs = File.Create(targetPath);
                stream.CopyTo(fs);
            }
        }
    }

    /// <summary>
    /// safely delete a mod folder from the working tree.
    /// validates path to prevent directory traversal attacks.
    /// </summary>
    public void DeleteModFolder(string folderName)
    {
        // path traversal guard — block ".." but allow nested relative paths (e.g. "SomeMod/inner")
        if (folderName.Contains(".."))
            throw new InvalidOperationException($"非法文件夹名：{folderName}");

        var fullPath = Path.Combine(WorkTreePath, folderName);
        var resolved = Path.GetFullPath(fullPath);
        var resolvedBase = Path.GetFullPath(WorkTreePath);

        // must be strictly inside WorkTreePath (not equal to it — never delete the root itself)
        if (!resolved.StartsWith(resolvedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("路径越界");

        if (!Directory.Exists(resolved))
            throw new InvalidOperationException($"文件夹不存在：{folderName}");

        FileSystemHelper.ForceDeleteDirectory(resolved);
    }
}
