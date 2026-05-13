using SyncTheSpire.Handlers;
using Xunit;

namespace SyncTheSpire.Tests.Handlers;

public class ConfigHandlerTests
{
    // ── NormalizePath ──────────────────────────────────────────────────────

    [Fact]
    public void NormalizePath_AbsoluteWithTrailingSep_StripsTrailingSep()
    {
        var result = ConfigHandler.NormalizePath("C:\\foo\\bar\\");
        Assert.Equal("C:\\foo\\bar", result);
    }

    [Fact]
    public void NormalizePath_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConfigHandler.NormalizePath(""));
    }

    [Fact]
    public void NormalizePath_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConfigHandler.NormalizePath(null));
    }

    [Fact]
    public void NormalizePath_AlreadyNormalized_ReturnsSame()
    {
        var path = "C:\\foo\\bar";
        Assert.Equal(path, ConfigHandler.NormalizePath(path));
    }

    // ── PathsEqual ─────────────────────────────────────────────────────────

    [Fact]
    public void PathsEqual_SameCase_ReturnsTrue()
    {
        Assert.True(ConfigHandler.PathsEqual("C:\\foo", "C:\\foo"));
    }

    [Fact]
    public void PathsEqual_DifferentCase_ReturnsTrueOnWindowsCaseInsensitive()
    {
        Assert.True(ConfigHandler.PathsEqual("C:\\Foo", "c:\\fOO"));
    }

    [Fact]
    public void PathsEqual_Different_ReturnsFalse()
    {
        Assert.False(ConfigHandler.PathsEqual("C:\\foo", "C:\\bar"));
    }

    // ── IsNestedPath ───────────────────────────────────────────────────────

    [Fact]
    public void IsNestedPath_AContainsB_ReturnsTrue()
    {
        // a is the parent, b lives inside a
        Assert.True(ConfigHandler.IsNestedPath("C:\\foo\\bar", "C:\\foo"));
    }

    [Fact]
    public void IsNestedPath_BContainsA_ReturnsTrue()
    {
        // checks both directions — order shouldn't matter
        Assert.True(ConfigHandler.IsNestedPath("C:\\foo", "C:\\foo\\bar"));
    }

    [Fact]
    public void IsNestedPath_SamePath_ReturnsTrue()
    {
        // same path is considered nested in either direction; semantically a "conflict"
        Assert.True(ConfigHandler.IsNestedPath("C:\\foo", "C:\\foo"));
    }

    [Fact]
    public void IsNestedPath_Sibling_ReturnsFalse()
    {
        Assert.False(ConfigHandler.IsNestedPath("C:\\foo\\a", "C:\\foo\\b"));
    }

    [Fact]
    public void IsNestedPath_DifferentCase_StillDetects()
    {
        Assert.True(ConfigHandler.IsNestedPath("C:\\Foo\\Bar", "c:\\foo"));
    }
}
