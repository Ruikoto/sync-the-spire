namespace SyncTheSpire.Services;

/// <summary>
/// lightweight file logger — date-based rotation, auto-cleanup, thread-safe.
/// static so every service can call it without constructor changes.
/// </summary>
public static class LogService
{
    private static readonly string LogDir =
        Path.Combine(ConfigService.AppDataDirPath, "Logs");

    private static readonly object WriteLock = new();

    private const int RetentionDays = 30;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    // log with full stack trace
    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}\n{ex}");

    public static void Error(Exception ex) =>
        Write("ERROR", ex.ToString());

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var now = DateTime.Now;
            var filePath = Path.Combine(LogDir, $"{now:yyyy-MM-dd}.log");
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

            lock (WriteLock)
            {
                File.AppendAllText(filePath, line);
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }

    /// <summary>
    /// delete log files older than retention period. call once at startup.
    /// </summary>
    public static void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;

            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(LogDir, "*.log"))
            {
                if (File.GetCreationTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
