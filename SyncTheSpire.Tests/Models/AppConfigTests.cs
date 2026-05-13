// AppConfig is marked [Obsolete] for production callers (V1 migration only),
// but we still want to assert its IsConfigured logic to lock the migration contract.
#pragma warning disable CS0618

using SyncTheSpire.Models;
using Xunit;

namespace SyncTheSpire.Tests.Models;

public class AppConfigTests
{
    [Fact]
    public void IsConfigured_AnonymousWithAllFields_ReturnsTrue()
    {
        var cfg = new AppConfig
        {
            Nickname = "ruikoto",
            RepoUrl = "https://github.com/foo/bar.git",
            GameInstallPath = "C:\\Games\\StS",
            AuthType = "anonymous"
        };

        Assert.True(cfg.IsConfigured);
    }

    [Fact]
    public void IsConfigured_HttpsMissingToken_ReturnsFalse()
    {
        var cfg = new AppConfig
        {
            Nickname = "ruikoto",
            RepoUrl = "https://github.com/foo/bar.git",
            GameInstallPath = "C:\\Games\\StS",
            AuthType = "https",
            Username = "alice",
            Token = ""
        };

        Assert.False(cfg.IsConfigured);
    }

    [Fact]
    public void IsConfigured_SshMissingKey_ReturnsFalse()
    {
        var cfg = new AppConfig
        {
            Nickname = "ruikoto",
            RepoUrl = "git@github.com:foo/bar.git",
            GameInstallPath = "C:\\Games\\StS",
            AuthType = "ssh",
            SshKeyPath = ""
        };

        Assert.False(cfg.IsConfigured);
    }

    [Fact]
    public void IsConfigured_AllBlank_ReturnsFalse()
    {
        Assert.False(new AppConfig().IsConfigured);
    }
}
