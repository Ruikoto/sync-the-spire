using System.Diagnostics;
using System.IO.Compression;

namespace SyncTheSpire.Services;

/// <summary>
/// resolves a working git.exe path: system PATH → bundled MinGit → cached MinGit → download on demand.
/// version pinned to v2.49.0, no auto-upgrade.
/// </summary>
public class GitResolver
{
    // bundled MinGit shipped alongside the exe (MSIX package or portable layout)
    private static readonly string BundledMinGitExePath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "mingit", "cmd", "git.exe");

    private static readonly string MinGitDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncTheSpire", "MinGit");
    private static readonly string MinGitExePath =
        Path.Combine(MinGitDir, "cmd", "git.exe");

    // proxy first, github fallback — version locked at 2.49.0
    private const string ProxyUrl =
        "https://sts-dl.rkto.cc/git-for-windows/git/releases/download/v2.49.0.windows.1/MinGit-2.49.0-64-bit.zip";
    private const string GitHubUrl =
        "https://github.com/git-for-windows/git/releases/download/v2.49.0.windows.1/MinGit-2.49.0-64-bit.zip";

    private string? _resolvedPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public record DownloadProgress(string Message, int? Percent = null);

    /// <summary>
    /// fires during MinGit download — wired up by MessageRouter for IPC forwarding
    /// </summary>
    public Action<DownloadProgress>? OnProgress { get; set; }

    /// <summary>
    /// get path to a working git.exe.
    /// checks: system PATH -> bundled MinGit -> cached MinGit -> downloads MinGit.
    /// blocks until resolved (always called from background threads).
    /// </summary>
    public string GetGitPath()
    {
        // fast path: already resolved this session
        if (_resolvedPath != null) return _resolvedPath;

        if (IsSystemGitAvailable())
        {
            _resolvedPath = "git";
            LogService.Info("Git resolved: system PATH");
            return _resolvedPath;
        }

        // bundled MinGit (MSIX / portable distribution)
        if (File.Exists(BundledMinGitExePath))
        {
            _resolvedPath = BundledMinGitExePath;
            LogService.Info($"Git resolved: bundled MinGit at {_resolvedPath}");
            return _resolvedPath;
        }

        if (File.Exists(MinGitExePath))
        {
            _resolvedPath = MinGitExePath;
            LogService.Info($"Git resolved: cached MinGit at {_resolvedPath}");
            return _resolvedPath;
        }

        // serialize concurrent callers — only one download at a time
        _lock.Wait();
        try
        {
            // double-check: another thread may have finished while we waited
            if (_resolvedPath != null) return _resolvedPath;
            if (File.Exists(BundledMinGitExePath))
            {
                _resolvedPath = BundledMinGitExePath;
                return _resolvedPath;
            }
            if (File.Exists(MinGitExePath))
            {
                _resolvedPath = MinGitExePath;
                return _resolvedPath;
            }

            DownloadAndExtract();
            _resolvedPath = MinGitExePath;
            return _resolvedPath;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool IsSystemGitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            return proc.WaitForExit(5000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void DownloadAndExtract()
    {
        LogService.Info("No git found, downloading MinGit...");
        var tempZip = Path.Combine(
            Path.GetTempPath(), $"SyncTheSpire_MinGit_{Guid.NewGuid():N}.zip");

        try
        {
            if (!TryDownload(ProxyUrl, tempZip) &&
                !TryDownload(GitHubUrl, tempZip))
            {
                throw new InvalidOperationException(
                    "无法下载 Git 组件，请检查网络连接后重试。\n" +
                    "或手动安装 Git：https://git-scm.com/download/win");
            }

            OnProgress?.Invoke(new DownloadProgress("正在解压 Git 组件..."));

            // clean up any partial extraction from a previous crash
            if (Directory.Exists(MinGitDir))
                Directory.Delete(MinGitDir, true);

            ZipFile.ExtractToDirectory(tempZip, MinGitDir);

            if (!File.Exists(MinGitExePath))
                throw new InvalidOperationException("Git 组件解压异常，未找到 git.exe");

            OnProgress?.Invoke(new DownloadProgress("Git 组件已就绪", 100));
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* best-effort cleanup */ }
        }
    }

    private bool TryDownload(string url, string destPath)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode) return false;

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var fs = File.Create(destPath);

            var buffer = new byte[81920];
            long downloaded = 0;
            int lastPercent = -1;
            var lastReport = DateTime.UtcNow;

            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                fs.Write(buffer, 0, bytesRead);
                downloaded += bytesRead;

                if (totalBytes > 0)
                {
                    var pct = (int)(downloaded * 100 / totalBytes);
                    var now = DateTime.UtcNow;
                    // throttle: report at most every 200ms or on 100%
                    if (pct != lastPercent &&
                        (pct == 100 || (now - lastReport).TotalMilliseconds >= 200))
                    {
                        lastPercent = pct;
                        lastReport = now;
                        OnProgress?.Invoke(new DownloadProgress(
                            $"正在下载 Git 组件... {pct}% ({FormatSize(downloaded)} / {FormatSize(totalBytes)})",
                            pct));
                    }
                }
                else
                {
                    // no content-length header: just show bytes downloaded
                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds >= 500)
                    {
                        lastReport = now;
                        OnProgress?.Invoke(new DownloadProgress(
                            $"正在下载 Git 组件... {FormatSize(downloaded)}"));
                    }
                }
            }

            return true;
        }
        catch (HttpRequestException ex)
        {
            LogService.Warn($"MinGit download failed from {url}: {ex.Message}");
            try { File.Delete(destPath); } catch { }
            return false;
        }
        catch (TaskCanceledException)
        {
            LogService.Warn($"MinGit download timed out from {url}");
            try { File.Delete(destPath); } catch { }
            return false;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
