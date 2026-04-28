namespace SyncTheSpire.Services;

/// <summary>
/// resolves paths to bundled git.exe and git-lfs.exe shipped under tools/mingit/.
/// no system-PATH lookup, no fallback, no download — both binaries must exist next to the app.
/// CI populates them during publish; local dev runs `pwsh ./SyncTheSpire/tools/fetch-mingit.ps1`.
/// </summary>
public class GitResolver
{
    private static readonly string ToolsDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "mingit");

    private static readonly string GitExePath =
        Path.Combine(ToolsDir, "mingw64", "bin", "git.exe");

    private static readonly string GitLfsExePath =
        Path.Combine(ToolsDir, "mingw64", "bin", "git-lfs.exe");

    public string GetGitPath()
    {
        if (!File.Exists(GitExePath))
            throw new FileNotFoundException(
                $"未找到内置 git.exe（期望路径：{GitExePath}）。安装包可能损坏，请重新安装。",
                GitExePath);
        return GitExePath;
    }

    public string GetGitLfsPath()
    {
        if (!File.Exists(GitLfsExePath))
            throw new FileNotFoundException(
                $"未找到内置 git-lfs.exe（期望路径：{GitLfsExePath}）。安装包可能损坏，请重新安装。",
                GitLfsExePath);
        return GitLfsExePath;
    }
}
