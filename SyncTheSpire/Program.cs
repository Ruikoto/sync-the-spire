using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using LibGit2Sharp;
using SyncTheSpire.Services;

namespace SyncTheSpire;

class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(false, "SyncTheSpire_SingleInstance");

        if (!mutex.WaitOne(0))
        {
            // another instance holds the mutex
            var current = Process.GetCurrentProcess();
            var existing = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);

            if (existing == null) return;

            if (ShouldReplaceExisting(existing))
            {
                // newer or nightly — kill old instance and take over
                existing.CloseMainWindow();
                if (!existing.WaitForExit(3000) && !existing.HasExited)
                    existing.Kill();

                // acquire the now-abandoned mutex
                try { mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { /* expected, we own it now */ }
            }
            else
            {
                // same or older — just activate the existing window
                var hwnd = existing.MainWindowHandle;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return;
            }
        }

        // repo lives in AppData which may trigger ownership check (CVE-2022-24765)
        GlobalSettings.SetOwnerValidation(false);

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        LogService.Info($"App starting v{version}");
        LogService.CleanupOldLogs();

        // catch everything that escapes normal error handling
        Application.ThreadException += (_, e) =>
            LogService.Error("Unhandled UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogService.Error("Unhandled domain exception", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogService.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    /// <summary>
    /// true if this new instance should replace the already-running one
    /// </summary>
    private static bool ShouldReplaceExisting(Process existing)
    {
        try
        {
            var myVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var theirVersion = FileVersionInfo.GetVersionInfo(existing.MainModule!.FileName!).ProductVersion ?? "unknown";

            // nightly build always takes over
            if (myVersion.StartsWith("nightly-")) return true;

            // replace an older nightly with a proper release
            if (theirVersion.StartsWith("nightly-")) return true;

            // both are release versions — replace only if strictly newer
            return CompareVersions(myVersion, theirVersion) > 0;
        }
        catch
        {
            // can't read the other process's info (e.g. permissions), play it safe
            return false;
        }
    }

    /// <summary>
    /// returns positive if a > b, 0 if equal, negative if a < b. expects vX.Y.Z format
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        static int[] Parse(string v) => v.TrimStart('v').Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var pa = Parse(a);
        var pb = Parse(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
