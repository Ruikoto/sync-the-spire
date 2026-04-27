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

    // ── unified dedup ───────────────────────────────────────────────────
    // local scans use file mtime (newer wins); branch scans have no mtime
    // and fall back to OrdinalIgnoreCase path order (first wins) — stable
    // across runs because git tree iteration is deterministic.

    private readonly record struct ModCandidate(ModInfo Mod, string SourceKey, DateTime? Mtime);

    /// <summary>
    /// dedupe a candidate list by mod id (case-insensitive).
    /// within an id group: prefer newest mtime, then lowest sourceKey as tiebreaker.
    /// returns the kept mods and a per-id duplicate report (empty when no collisions).
    /// </summary>
    private static (List<ModInfo> Kept, List<ModDuplicateInfo> Duplicates) DedupById(
        IEnumerable<ModCandidate> candidates)
    {
        var kept = new List<ModInfo>();
        var duplicates = new List<ModDuplicateInfo>();

        foreach (var group in candidates.GroupBy(c => c.Mod.Id!, StringComparer.OrdinalIgnoreCase))
        {
            // newest mtime first; missing mtime sorts last; sourceKey as stable tiebreaker
            var ordered = group
                .OrderByDescending(c => c.Mtime ?? DateTime.MinValue)
                .ThenBy(c => c.SourceKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var winner = ordered[0];
            kept.Add(winner.Mod);

            if (ordered.Count > 1)
            {
                duplicates.Add(new ModDuplicateInfo(
                    Id: group.Key,
                    KeptPath: winner.SourceKey,
                    AllPaths: ordered.Select(c => c.SourceKey).ToList()));
            }
        }

        return (kept, duplicates);
    }

    // ── local working tree scan ─────────────────────────────────────────

    /// <summary>
    /// scan the local working tree for mod definitions (filesystem equivalent of GetBranchMods).
    /// walks all .json files under WorkTreePath and keeps entries with a valid "id" field.
    /// duplicate ids are deduplicated by file mtime — newest wins.
    /// </summary>
    public List<ModInfo> GetLocalMods()
    {
        var (kept, _) = ScanLocalCandidates();
        return kept;
    }

    /// <summary>
    /// shared local-side scan that produces the deduped list and the duplicate report together.
    /// </summary>
    private (List<ModInfo> Kept, List<ModDuplicateInfo> Duplicates) ScanLocalCandidates()
    {
        if (!Directory.Exists(WorkTreePath))
            return ([], []);

        var candidates = new List<ModCandidate>();

        foreach (var file in Directory.EnumerateFiles(WorkTreePath, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(file);
                var mod = JsonSerializer.Deserialize<ModInfo>(json, ModJsonOpts);
                if (string.IsNullOrEmpty(mod?.Id)) continue;

                DateTime? mtime = null;
                try { mtime = File.GetLastWriteTimeUtc(file); } catch { /* best effort */ }
                candidates.Add(new ModCandidate(mod, file, mtime));
            }
            catch
            {
                // not a mod definition or malformed json, skip
            }
        }

        return DedupById(candidates);
    }

    // ── remote branch tree scan ─────────────────────────────────────────

    /// <summary>
    /// read all mod definitions from a remote branch's file tree without checkout.
    /// walks the commit tree recursively, tries to deserialize every .json blob,
    /// and keeps entries that have a valid "id" field.
    /// duplicate ids are deduplicated by tree path (lowest path wins) — git blobs have no mtime.
    /// </summary>
    public List<ModInfo> GetBranchMods(string branchName)
    {
        using var repo = OpenRepo();
        var branch = repo.Branches[$"origin/{branchName}"];
        if (branch is null) return [];

        var candidates = new List<ModCandidate>();
        ScanTreeForMods(branch.Tip.Tree, "", candidates);

        var (kept, _) = DedupById(candidates);
        return kept;
    }

    private static void ScanTreeForMods(Tree tree, string pathPrefix, List<ModCandidate> candidates)
    {
        foreach (var entry in tree)
        {
            var entryPath = string.IsNullOrEmpty(pathPrefix) ? entry.Name : $"{pathPrefix}/{entry.Name}";
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                ScanTreeForMods((Tree)entry.Target, entryPath, candidates);
            }
            else if (entry.TargetType == TreeEntryTargetType.Blob &&
                     entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var blob = (Blob)entry.Target;
                    var mod = JsonSerializer.Deserialize<ModInfo>(blob.GetContentText(), ModJsonOpts);
                    if (string.IsNullOrEmpty(mod?.Id)) continue;
                    // mtime null — git blobs don't carry one; tree path serves as stable tiebreaker
                    candidates.Add(new ModCandidate(mod, entryPath, null));
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
    /// shares the same dedup as GetLocalMods (mtime-based, newest wins) and additionally returns
    /// a per-id duplicate report so the UI can warn about manifests that point to the same mod.
    /// </summary>
    public (List<ModInfo> Mods, List<ModDuplicateInfo> Duplicates) GetLocalModsDetailed()
    {
        if (!Directory.Exists(WorkTreePath)) return ([], []);

        // collect candidates with their source paths so we can locate the kept manifest later
        var candidates = new List<ModCandidate>();
        foreach (var file in Directory.EnumerateFiles(WorkTreePath, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(file);
                var mod = JsonSerializer.Deserialize<ModInfo>(json, ModJsonOpts);
                if (string.IsNullOrEmpty(mod?.Id)) continue;

                DateTime? mtime = null;
                try { mtime = File.GetLastWriteTimeUtc(file); } catch { /* best effort */ }
                candidates.Add(new ModCandidate(mod, file, mtime));
            }
            catch { /* not a valid manifest, skip */ }
        }

        var (kept, duplicates) = DedupById(candidates);

        // index kept mods back to their manifest path for the enrichment pass
        var keptByModRef = new Dictionary<ModInfo, string>(ReferenceEqualityComparer.Instance);
        foreach (var c in candidates)
        {
            if (kept.Contains(c.Mod, ReferenceEqualityComparer.Instance) && !keptByModRef.ContainsKey(c.Mod))
                keptByModRef[c.Mod] = c.SourceKey;
        }

        var result = new List<ModInfo>();
        foreach (var mod in kept)
        {
            if (!keptByModRef.TryGetValue(mod, out var jsonPath)) continue;

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

        return (result, duplicates);
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

    // ── duplicate manifest cleanup ─────────────────────────────────────

    /// <summary>
    /// remove duplicate manifest .json files, keeping the one that won the dedup
    /// (newest mtime). does NOT touch non-manifest files in mod folders — only
    /// the redundant .json itself, which is what causes in-game multiplayer errors.
    /// returns the absolute paths actually deleted.
    /// </summary>
    public List<string> CleanLocalDuplicateManifests()
    {
        var (_, duplicates) = ScanLocalCandidates();
        var deleted = new List<string>();

        foreach (var dup in duplicates)
        {
            foreach (var path in dup.AllPaths)
            {
                if (string.Equals(path, dup.KeptPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn($"Failed to delete duplicate manifest {path}: {ex.Message}");
                }
            }
        }

        return deleted;
    }

    /// <summary>
    /// look up an existing local mod by id and remove its containing folder.
    /// used by install paths to enforce "newest wins" — overwrite an older mod
    /// with the same id rather than letting two manifests coexist.
    /// returns true if a mod was found and deleted, false if no match.
    /// </summary>
    public bool RemoveLocalModById(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;

        var (mods, _) = GetLocalModsDetailed();
        var match = mods.FirstOrDefault(m =>
            string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (match == null || string.IsNullOrEmpty(match.FolderPath)) return false;

        var resolvedBase = Path.GetFullPath(WorkTreePath);
        var resolvedDir = Path.GetFullPath(match.FolderPath);

        // edge case: manifest sits directly at worktree root → just delete the .json,
        // never the worktree itself
        if (string.Equals(resolvedDir, resolvedBase, StringComparison.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(match.FolderPath, $"{match.FolderName}.json");
            try { if (File.Exists(manifestPath)) { File.Delete(manifestPath); return true; } }
            catch (Exception ex) { LogService.Warn($"Failed to delete root manifest {manifestPath}: {ex.Message}"); }
            return false;
        }

        // safety: must be strictly inside worktree
        if (!resolvedDir.StartsWith(resolvedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Directory.Exists(resolvedDir)) return false;

        FileSystemHelper.ForceDeleteDirectory(resolvedDir);
        return true;
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

        // collect (mod, topLevelFolder, treePath) candidates so we can reuse the unified dedup
        var candidates = new List<ModCandidate>();
        var topFolderByMod = new Dictionary<ModInfo, string>(ReferenceEqualityComparer.Instance);

        foreach (var entry in tree)
        {
            if (entry.TargetType != TreeEntryTargetType.Tree) continue;
            var topFolder = entry.Name;
            CollectTreeManifests((Tree)entry.Target, topFolder, topFolder, candidates, topFolderByMod);
        }

        var (kept, _) = DedupById(candidates);
        return kept
            .Select(m => topFolderByMod.TryGetValue(m, out var tf) ? m with { FolderName = tf } : m)
            .ToList();
    }

    /// <summary>
    /// recursively walk a git tree, collecting manifest candidates and remembering
    /// each mod's top-level folder for the branch-copy feature.
    /// </summary>
    private static void CollectTreeManifests(
        Tree tree,
        string topLevelFolder,
        string pathPrefix,
        List<ModCandidate> candidates,
        Dictionary<ModInfo, string> topFolderByMod)
    {
        foreach (var entry in tree)
        {
            var entryPath = string.IsNullOrEmpty(pathPrefix) ? entry.Name : $"{pathPrefix}/{entry.Name}";
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                CollectTreeManifests((Tree)entry.Target, topLevelFolder, entryPath, candidates, topFolderByMod);
            }
            else if (entry.TargetType == TreeEntryTargetType.Blob
                     && entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var blob = (Blob)entry.Target;
                    var mod = JsonSerializer.Deserialize<ModInfo>(blob.GetContentText(), ModJsonOpts);
                    if (string.IsNullOrEmpty(mod?.Id)) continue;
                    candidates.Add(new ModCandidate(mod, entryPath, null));
                    topFolderByMod[mod] = topLevelFolder;
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
