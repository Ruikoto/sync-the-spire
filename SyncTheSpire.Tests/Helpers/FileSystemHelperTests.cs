using SyncTheSpire.Helpers;
using Xunit;

namespace SyncTheSpire.Tests.Helpers;

public class FileSystemHelperTests
{
    // The predicate overload doesn't touch the disk unless the predicate does — pass
    // synthetic predicates against synthetic paths for true unit-test isolation.

    [Fact]
    public void FindAncestorContaining_PredicateMatchesStart_ReturnsStart()
    {
        var start = "C:\\foo\\bar\\baz";

        var result = FileSystemHelper.FindAncestorContaining(start, dir => dir == start);

        Assert.Equal(start, result);
    }

    [Fact]
    public void FindAncestorContaining_PredicateMatchesIntermediate_ReturnsIntermediate()
    {
        var start = "C:\\foo\\bar\\baz";
        var target = "C:\\foo";

        var result = FileSystemHelper.FindAncestorContaining(start, dir =>
            string.Equals(dir, target, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(target, result);
    }

    [Fact]
    public void FindAncestorContaining_PredicateNeverMatches_ReturnsNull()
    {
        var result = FileSystemHelper.FindAncestorContaining("C:\\foo\\bar", _ => false);

        Assert.Null(result);
    }

    [Fact]
    public void FindAncestorContaining_PredicateMatchesRoot_StopsAtRoot()
    {
        // walking up from a drive root should still try the root itself, then exit when
        // GetParent returns the same path; the loop must not spin.
        var root = "C:\\";

        var result = FileSystemHelper.FindAncestorContaining(root, dir => dir == root);

        Assert.Equal(root, result);
    }
}
