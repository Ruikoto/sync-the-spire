using System.IO.Compression;
using System.Security.Cryptography;

namespace SyncTheSpire.Services;

/// <summary>
/// extracts the embedded MinGit bundle to %LocalAppData%\SyncTheSpire\MinGit\ on first run.
/// hash-sentinel short-circuits subsequent launches. cross-process mutex serializes concurrent
/// app starts. for MSIX builds the LocalApplicationData path is auto-redirected into the
/// package container, so the same code works without modification.
/// </summary>
public static class MinGitExtractor
{
    private const string ResourceName = "MinGitBundle.zip";
    private const string SentinelName = ".bundle-hash";
    // Local\ = per-session scope. ExtractDir is per-user (LocalApplicationData), so cross-user
    // serialization (Global\) would be overly broad. Per-session is enough since Program.Main's
    // single-instance mutex already prevents same-user same-session concurrency from reaching here.
    private const string MutexName    = @"Local\SyncTheSpire.MinGitExtract";

    private static readonly string ExtractDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SyncTheSpire", "MinGit");

    private static readonly Lazy<Task<string>> _task = new(() => Task.Run(EnsureSync));

    public static Task<string> EnsureExtractedAsync() => _task.Value;

    private static string EnsureSync()
    {
        var asm = typeof(MinGitExtractor).Assembly;
        var resourceFullName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("MinGitBundle.zip embedded resource missing — broken build.");

        // load the resource into memory once: hash + extract from the same byte buffer
        // (manifest resource streams are non-seekable, so we can't rewind and re-read)
        byte[] bundle;
        using (var s = asm.GetManifestResourceStream(resourceFullName)!)
        using (var ms = new MemoryStream((int)s.Length))
        {
            s.CopyTo(ms);
            bundle = ms.ToArray();
        }
        var hashHex = Convert.ToHexString(SHA256.HashData(bundle));

        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
            catch (AbandonedMutexException) { acquired = true; /* prior process crashed mid-extract — we own it now */ }
            if (!acquired)
                throw new TimeoutException("等待 MinGit 解压锁超时（2 分钟）。");

            var sentinel = Path.Combine(ExtractDir, SentinelName);
            if (File.Exists(sentinel) &&
                string.Equals(File.ReadAllText(sentinel).Trim(), hashHex, StringComparison.Ordinal) &&
                File.Exists(Path.Combine(ExtractDir, "mingw64", "bin", "git.exe")))
            {
                return ExtractDir;
            }

            // wipe — covers legacy pre-08e5dd5 network-downloaded MinGit, partial extraction
            // from a crashed prior run, and stale content from a previous version bump.
            if (Directory.Exists(ExtractDir))
                Directory.Delete(ExtractDir, recursive: true);
            Directory.CreateDirectory(ExtractDir);

            using (var ms = new MemoryStream(bundle, writable: false))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(ExtractDir);
            }

            // sentinel last — anything that crashes before this leaves no valid sentinel
            // and gets wiped on next launch
            File.WriteAllText(sentinel, hashHex);
            return ExtractDir;
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}
