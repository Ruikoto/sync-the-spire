using SyncTheSpire.Models;
using Xunit;

namespace SyncTheSpire.Tests.Models;

public class WorkspaceConfigTests
{
    private static WorkspaceConfig MakeFullyConfiguredAnonymous() => new()
    {
        Nickname = "ruikoto",
        RepoUrl = "https://github.com/foo/bar.git",
        GameInstallPath = "C:\\Games\\StS2",
        AuthType = "anonymous"
    };

    // ── IsConfigured ───────────────────────────────────────────────────────

    [Fact]
    public void IsConfigured_AnonymousWithAllRequiredFields_ReturnsTrue()
    {
        Assert.True(MakeFullyConfiguredAnonymous().IsConfigured);
    }

    [Fact]
    public void IsConfigured_HttpsMissingToken_ReturnsFalse()
    {
        var ws = MakeFullyConfiguredAnonymous();
        ws.AuthType = "https";
        ws.Username = "alice";
        ws.Token = "";

        Assert.False(ws.IsConfigured);
    }

    [Fact]
    public void IsConfigured_HttpsMissingUsername_ReturnsFalse()
    {
        var ws = MakeFullyConfiguredAnonymous();
        ws.AuthType = "https";
        ws.Username = "";
        ws.Token = "abcdef";

        Assert.False(ws.IsConfigured);
    }

    [Fact]
    public void IsConfigured_SshMissingKeyPath_ReturnsFalse()
    {
        var ws = MakeFullyConfiguredAnonymous();
        ws.AuthType = "ssh";
        ws.SshKeyPath = "";

        Assert.False(ws.IsConfigured);
    }

    [Fact]
    public void IsConfigured_MissingNickname_ReturnsFalse()
    {
        var ws = MakeFullyConfiguredAnonymous();
        ws.Nickname = "  ";

        Assert.False(ws.IsConfigured);
    }

    [Fact]
    public void IsConfigured_BlankAllFields_ReturnsFalse()
    {
        var ws = new WorkspaceConfig();
        Assert.False(ws.IsConfigured);
    }

    // ── GameModPath ────────────────────────────────────────────────────────

    [Fact]
    public void GameModPath_Sts2WithInstallPath_AppendsModsFolder()
    {
        var ws = new WorkspaceConfig
        {
            GameType = "sts2",
            GameInstallPath = "C:\\Games\\StS2"
        };

        Assert.Equal(Path.Combine("C:\\Games\\StS2", "Mods"), ws.GameModPath);
    }

    [Fact]
    public void GameModPath_GenericGame_UsesInstallPathAsIs()
    {
        var ws = new WorkspaceConfig
        {
            GameType = "generic",
            GameInstallPath = "C:\\Games\\Foo"
        };

        Assert.Equal("C:\\Games\\Foo", ws.GameModPath);
    }

    [Fact]
    public void GameModPath_EmptyInstallPath_FallsBackToLegacyField()
    {
        var ws = new WorkspaceConfig
        {
            GameInstallPath = "",
            GameModPathLegacy = "D:\\OldPath\\Mods"
        };

        Assert.Equal("D:\\OldPath\\Mods", ws.GameModPath);
    }

    [Fact]
    public void GameModPath_BothEmpty_ReturnsEmpty()
    {
        var ws = new WorkspaceConfig();
        Assert.Equal(string.Empty, ws.GameModPath);
    }
}
