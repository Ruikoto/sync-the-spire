using System.Text.Json;
using LibGit2Sharp;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

/// <summary>
/// Scans git branch trees for NSFW signals (branch names, folder names, mod names).
/// Extracted from GitService to keep content detection separate from git ops.
/// </summary>
public class NsfwDetectionService
{
    private readonly ConfigService _config;

    // ordered longest-first so "R18G" matches before "R18"
    private static readonly string[] NsfwKeywords = ["r18-g", "r18g", "nsfw", "r18"];

    private static readonly JsonSerializerOptions ModJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public record NsfwResult(bool IsNsfw, List<string> Reasons);

    public NsfwDetectionService(ConfigService config)
    {
        _config = config;
    }

    private Repository OpenRepo() => new(_config.GitDirPath);

    /// <summary>
    /// scan each branch for NSFW signals: branch name, folder names, and mod names.
    /// pure object-db read, no checkout involved. opens a fresh Repository — for callers
    /// that already have one, prefer the overload that takes an external Repository.
    /// </summary>
    public Dictionary<string, NsfwResult> CheckBranchesNsfw(IEnumerable<string> branchNames)
    {
        using var repo = OpenRepo();
        return CheckBranchesNsfw(branchNames, repo);
    }

    /// <summary>
    /// same as CheckBranchesNsfw but reuses a caller-supplied Repository to avoid
    /// the file-lock + index-load cost of opening one per call (HandleGetBranches is hot).
    /// </summary>
    public Dictionary<string, NsfwResult> CheckBranchesNsfw(IEnumerable<string> branchNames, Repository repo)
    {
        var result = new Dictionary<string, NsfwResult>();

        foreach (var name in branchNames)
        {
            var reasons = new List<string>();

            var kw = MatchNsfwKeyword(name);
            if (kw != null)
                reasons.Add($"分支名称包含「{kw}」");

            var branch = repo.Branches[$"origin/{name}"];
            if (branch != null)
                ScanTreeForNsfw(branch.Tip.Tree, reasons);

            result[name] = new NsfwResult(reasons.Count > 0, reasons);
        }

        return result;
    }

    private static string? MatchNsfwKeyword(string text)
    {
        foreach (var keyword in NsfwKeywords)
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return keyword.ToUpperInvariant();
        return null;
    }

    private static void ScanTreeForNsfw(Tree tree, List<string> reasons)
    {
        foreach (var entry in tree)
        {
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                var kw = MatchNsfwKeyword(entry.Name);
                if (kw != null)
                    reasons.Add($"文件夹「{entry.Name}」包含「{kw}」");

                ScanTreeForNsfw((Tree)entry.Target, reasons);
            }
            else if (entry.TargetType == TreeEntryTargetType.Blob &&
                     entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var blob = (Blob)entry.Target;
                    var mod = JsonSerializer.Deserialize<ModInfo>(blob.GetContentText(), ModJsonOpts);
                    if (!string.IsNullOrEmpty(mod?.Id) && !string.IsNullOrEmpty(mod?.Name))
                    {
                        var kw = MatchNsfwKeyword(mod.Name);
                        if (kw != null)
                            reasons.Add($"Mod「{mod.Name}」名称包含「{kw}」");
                    }
                }
                catch
                {
                    // skip non-mod or malformed json
                }
            }
        }
    }
}
