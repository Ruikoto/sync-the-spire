namespace SyncTheSpire.Models;

/// <summary>
/// reports a single mod id that has more than one manifest .json on disk.
/// AllPaths is every manifest path found for that id; KeptPath is the one
/// the dedup picked as authoritative (newest mtime wins for local scans).
/// </summary>
public sealed record ModDuplicateInfo(
    string Id,
    string KeptPath,
    IReadOnlyList<string> AllPaths);
