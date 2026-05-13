using SyncTheSpire.Models;
using SyncTheSpire.Services;
using Xunit;

namespace SyncTheSpire.Tests.Services;

public class ModScannerServiceTests
{
    private static ModScannerService.ModCandidate Cand(string id, string sourceKey, DateTime? mtime) =>
        new(new ModInfo { Id = id, Name = id }, sourceKey, mtime);

    [Fact]
    public void DedupById_Empty_ReturnsEmptyKeptAndDuplicates()
    {
        var (kept, dups) = ModScannerService.DedupById([]);

        Assert.Empty(kept);
        Assert.Empty(dups);
    }

    [Fact]
    public void DedupById_SingleEntry_ReturnsItWithoutDuplicateReport()
    {
        var c = Cand("modA", "path/a.json", DateTime.UtcNow);
        var (kept, dups) = ModScannerService.DedupById([c]);

        Assert.Single(kept);
        Assert.Equal("modA", kept[0].Id);
        Assert.Empty(dups);
    }

    [Fact]
    public void DedupById_DuplicateIds_PrefersNewestMtime()
    {
        var older = Cand("modA", "old/a.json", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Cand("modA", "new/a.json", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (kept, dups) = ModScannerService.DedupById([older, newer]);

        Assert.Single(kept);
        var dup = Assert.Single(dups);
        Assert.Equal("new/a.json", dup.KeptPath);
        Assert.Equal(2, dup.AllPaths.Count);
    }

    [Fact]
    public void DedupById_AllMtimesNull_FallsBackToSourceKeyOrder()
    {
        // sourceKey ascending → "a/x.json" wins over "b/x.json"
        var first = Cand("modA", "b/x.json", null);
        var second = Cand("modA", "a/x.json", null);
        var (kept, dups) = ModScannerService.DedupById([first, second]);

        var dup = Assert.Single(dups);
        Assert.Equal("a/x.json", dup.KeptPath);
        Assert.Single(kept);
    }

    [Fact]
    public void DedupById_IdCaseInsensitive_GroupsTogether()
    {
        var lower = Cand("modA", "lower.json", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var upper = Cand("MODA", "upper.json", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (kept, dups) = ModScannerService.DedupById([lower, upper]);

        Assert.Single(kept);
        var dup = Assert.Single(dups);
        Assert.Equal("upper.json", dup.KeptPath);
    }

    [Fact]
    public void DedupById_DistinctIds_KeepsAllNoDuplicates()
    {
        var a = Cand("modA", "a.json", DateTime.UtcNow);
        var b = Cand("modB", "b.json", DateTime.UtcNow);
        var (kept, dups) = ModScannerService.DedupById([a, b]);

        Assert.Equal(2, kept.Count);
        Assert.Empty(dups);
    }

    [Fact]
    public void DedupById_DuplicateReport_IncludesAllSourceKeys()
    {
        var c1 = Cand("modA", "a.json", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var c2 = Cand("modA", "b.json", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var c3 = Cand("modA", "c.json", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (_, dups) = ModScannerService.DedupById([c1, c2, c3]);

        var dup = Assert.Single(dups);
        Assert.Equal(3, dup.AllPaths.Count);
        Assert.Equal("c.json", dup.KeptPath);
    }
}
