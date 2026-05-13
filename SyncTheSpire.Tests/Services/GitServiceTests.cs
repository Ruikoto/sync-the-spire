using SyncTheSpire.Models;
using SyncTheSpire.Services;
using Xunit;

namespace SyncTheSpire.Tests.Services;

public class GitServiceTests
{
    // ── IsProtectedBranch ──────────────────────────────────────────────────

    [Theory]
    [InlineData("main", true)]
    [InlineData("master", true)]
    [InlineData("MAIN", true)]
    [InlineData("Master", true)]
    [InlineData("feature/foo", false)]
    [InlineData("", false)]
    [InlineData("_init", false)]
    public void IsProtectedBranch_VariousNames_MatchesExpected(string branchName, bool expected)
    {
        Assert.Equal(expected, GitService.IsProtectedBranch(branchName));
    }

    // ── GetRepoHost ────────────────────────────────────────────────────────

    [Fact]
    public void GetRepoHost_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GitService.GetRepoHost(""));
    }

    [Fact]
    public void GetRepoHost_HttpsGithub_ReturnsLowercaseHost()
    {
        Assert.Equal("github.com", GitService.GetRepoHost("https://github.com/foo/bar.git"));
    }

    [Fact]
    public void GetRepoHost_HttpScheme_ReturnsLowercaseHost()
    {
        Assert.Equal("gitee.com", GitService.GetRepoHost("http://gitee.com/foo/bar"));
    }

    [Fact]
    public void GetRepoHost_SshGithub_ParsesAtColonForm()
    {
        Assert.Equal("github.com", GitService.GetRepoHost("git@github.com:foo/bar"));
    }

    [Fact]
    public void GetRepoHost_SshGiteeWithSuffix_ParsesAtColonForm()
    {
        Assert.Equal("gitee.com", GitService.GetRepoHost("git@gitee.com:foo/bar.git"));
    }

    [Fact]
    public void GetRepoHost_MixedCase_LowercasesHost()
    {
        Assert.Equal("github.com", GitService.GetRepoHost("https://GitHub.com/foo/bar"));
    }

    [Fact]
    public void GetRepoHost_Malformed_FallsBackToLowercasedInput()
    {
        // not a URI, no git@ prefix → fallthrough returns the input lowercased.
        // downstream host-substring checks (github.com / gitee.com) just won't match,
        // which is the intended graceful degradation.
        Assert.Equal("not-a-url", GitService.GetRepoHost("Not-A-URL"));
    }

    // ── FormatNames ────────────────────────────────────────────────────────

    [Fact]
    public void FormatNames_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GitService.FormatNames(new HashSet<string>()));
    }

    [Fact]
    public void FormatNames_SingleEntry_ReturnsName()
    {
        Assert.Equal("alpha", GitService.FormatNames(new HashSet<string> { "alpha" }));
    }

    [Fact]
    public void FormatNames_ThreeEntries_JoinsAlphabetical()
    {
        var result = GitService.FormatNames(new HashSet<string> { "charlie", "alpha", "bravo" });
        Assert.Equal("alpha, bravo, charlie", result);
    }

    [Fact]
    public void FormatNames_FourEntries_AppendsOneMore()
    {
        var result = GitService.FormatNames(new HashSet<string> { "delta", "alpha", "bravo", "charlie" });
        Assert.Equal("alpha, bravo, charlie (+1 more)", result);
    }

    [Fact]
    public void FormatNames_ManyEntries_AppendsCount()
    {
        var names = new HashSet<string> { "a", "b", "c", "d", "e", "f", "g" };
        var result = GitService.FormatNames(names);
        Assert.Equal("a, b, c (+4 more)", result);
    }

    // ── MakeSignature ──────────────────────────────────────────────────────

    [Fact]
    public void MakeSignature_NicknameProvided_UsesNickname()
    {
        var ws = new WorkspaceConfig { Nickname = "ruikoto" };
        var sig = GitService.MakeSignature(ws, "user@example.com");

        Assert.Equal("ruikoto", sig.Name);
        Assert.Equal("user@example.com", sig.Email);
    }

    [Fact]
    public void MakeSignature_BlankNickname_FallsBackToPlayer()
    {
        var ws = new WorkspaceConfig { Nickname = "   " };
        var sig = GitService.MakeSignature(ws, "user@example.com");

        Assert.Equal("player", sig.Name);
    }

    [Fact]
    public void MakeSignature_NullEmail_BuildsSyntheticDomain()
    {
        var ws = new WorkspaceConfig { Nickname = "ruikoto" };
        var sig = GitService.MakeSignature(ws, null);

        Assert.Equal("ruikoto@sync-the-spire", sig.Email);
    }

    [Fact]
    public void MakeSignature_NullEmailBlankNickname_UsesPlayerDomain()
    {
        var ws = new WorkspaceConfig { Nickname = "" };
        var sig = GitService.MakeSignature(ws, null);

        Assert.Equal("player", sig.Name);
        Assert.Equal("player@sync-the-spire", sig.Email);
    }
}
