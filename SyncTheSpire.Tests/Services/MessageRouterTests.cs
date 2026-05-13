using SyncTheSpire.Services;
using Xunit;

namespace SyncTheSpire.Tests.Services;

public class MessageRouterTests
{
    [Fact]
    public void FriendlyGitError_GithubConnFailure_AppendsChinaPlatformHint()
    {
        var raw = "fatal: unable to access 'https://github.com/foo/bar.git/': Could not resolve host: github.com";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("无法连接到远程仓库", result);
        Assert.Contains("AtomGit", result);
    }

    [Fact]
    public void FriendlyGitError_NonGithubConnFailure_OmitsChinaPlatformHint()
    {
        var raw = "fatal: unable to access 'https://atomgit.com/foo/bar.git/': Connection refused";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("无法连接到远程仓库", result);
        Assert.DoesNotContain("AtomGit", result);
    }

    [Fact]
    public void FriendlyGitError_SslCertificate_ReturnsSslHint()
    {
        var raw = "SSL certificate problem: self signed certificate";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("SSL", result);
    }

    [Fact]
    public void FriendlyGitError_RepoNotFound404_ReturnsRepoNotFoundHint()
    {
        var raw = "remote: Repository not found.\nfatal: 404";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("仓库未找到", result);
    }

    [Fact]
    public void FriendlyGitError_RepoSizeLimit_ReturnsCleanupHint()
    {
        var raw = "remote: error: Repository size limit exceeded";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("云端仓库已满", result);
        Assert.Contains("清理分支历史", result);
    }

    [Fact]
    public void FriendlyGitError_GiteeOverQuota_ReturnsCleanupHint()
    {
        var raw = "remote: error: repository over quota";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("云端仓库已满", result);
    }

    [Fact]
    public void FriendlyGitError_LfsQuotaExceeded_ReturnsLfsHint()
    {
        var raw = "LFS: quota exceeded for object";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("LFS 配额", result);
    }

    [Fact]
    public void FriendlyGitError_SingleFileSizeRejection_ReturnsSizeHint()
    {
        var raw = "remote: error: File foo.bin: 150 MB exceeds the maximum file size of 100 MB";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("单文件上传限制", result);
    }

    [Fact]
    public void FriendlyGitError_ShallowUpdate_ReturnsShallowHint()
    {
        var raw = "remote: error: shallow update not allowed";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("浅克隆", result);
    }

    [Fact]
    public void FriendlyGitError_PreReceiveHook_TruncatesToTail()
    {
        var prefix = new string('x', 500);
        var raw = $"{prefix}remote: error: pre-receive hook declined: custom rule violation";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Contains("远端仓库拒绝了此次推送", result);
        Assert.Contains("…", result);
        // tail body capped to 300 chars + ellipsis prefix
        Assert.True(result.Length < raw.Length);
    }

    [Fact]
    public void FriendlyGitError_FailedPrefix_StripsAndReturnsCleanDetail()
    {
        var raw = "git clone failed: Cloning into 'D:\\foo'...\nfatal: bad things happened";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.StartsWith("Git 操作失败：", result);
        Assert.DoesNotContain("Cloning into", result);
        Assert.Contains("bad things happened", result);
    }

    [Fact]
    public void FriendlyGitError_GenericFallthrough_WrapsRawMessage()
    {
        var raw = "something completely unexpected blew up";
        var result = MessageRouter.FriendlyGitError(raw);

        Assert.Equal($"Git 操作失败：{raw}", result);
    }
}
