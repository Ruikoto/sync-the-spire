namespace SyncTheSpire.Services;

/// <summary>
/// resolves paths to bundled git.exe and git-lfs.exe. delegates extraction to MinGitExtractor;
/// the binaries always live under %LocalAppData%\SyncTheSpire\MinGit\ (auto-redirected into
/// the MSIX package container when running packaged).
/// </summary>
public class GitResolver
{
    // resolved once per process. blocks the calling (background) thread if extraction
    // is still in flight — UI thread should never reach here directly because all git
    // ops are dispatched from async handlers.
    private static readonly Lazy<string> _toolsDir = new(() =>
        MinGitExtractor.EnsureExtractedAsync().GetAwaiter().GetResult());

    public string GetGitPath()
    {
        var path = Path.Combine(_toolsDir.Value, "mingw64", "bin", "git.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"未找到内置 git.exe（期望路径：{path}）。安装包可能损坏，请重新安装。",
                path);
        return path;
    }

    public string GetGitLfsPath()
    {
        var path = Path.Combine(_toolsDir.Value, "mingw64", "bin", "git-lfs.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"未找到内置 git-lfs.exe（期望路径：{path}）。安装包可能损坏，请重新安装。",
                path);
        return path;
    }
}
