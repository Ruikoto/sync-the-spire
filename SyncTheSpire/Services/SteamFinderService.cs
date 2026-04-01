using Microsoft.Win32;

namespace SyncTheSpire.Services;

public class SteamFinderService
{
    private const int StS2AppId = 2868840;

    public record SteamAccount(
        string SteamId64,
        string PersonaName,
        bool MostRecent,
        bool HasSaveFolder
    );

    public record GamePathResult(string? Path, string? Error);

    public record SavePathResult(
        string? BasePath,
        List<SteamAccount>? Accounts,
        string? Error
    );

    /// <summary>
    /// Find the StS2 install directory via Steam registry + VDF files.
    /// </summary>
    public GamePathResult FindGamePath()
    {
        var steamPath = GetSteamPath();
        if (steamPath is null)
            return new GamePathResult(null, "未检测到 Steam 安装，请确认 Steam 已安装");

        var libraries = GetLibraryPaths(steamPath);
        if (libraries.Count == 0)
            return new GamePathResult(null, "无法读取 Steam 库信息");

        foreach (var lib in libraries)
        {
            var installDir = GetInstallDir(lib);
            if (installDir is null) continue;

            var fullPath = Path.Combine(lib, "steamapps", "common", installDir);
            if (Directory.Exists(fullPath))
                return new GamePathResult(fullPath, null);

            // manifest exists but folder is missing — try other libraries first
            continue;
        }

        return new GamePathResult(null, "未在 Steam 库中找到 Slay the Spire 2");
    }

    /// <summary>
    /// Find Steam accounts that might own saves, cross-referenced with
    /// existing save folders under %APPDATA%\SlayTheSpire2\steam\.
    /// </summary>
    public SavePathResult FindSaveAccounts()
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2", "steam");

        if (!Directory.Exists(basePath))
            return new SavePathResult(null, null, "未找到存档目录，请确认游戏已运行过至少一次");

        // all steamId64 subdirectories that actually exist
        var existingIds = new HashSet<string>(
            Directory.GetDirectories(basePath)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.All(char.IsDigit) && n.Length >= 10)!
        );

        if (existingIds.Count == 0)
            return new SavePathResult(null, null, "未找到任何存档文件夹");

        // try to get Steam user info for richer display
        var steamPath = GetSteamPath();
        var steamUsers = steamPath is not null ? GetSteamUsers(steamPath) : [];

        var accounts = new List<SteamAccount>();
        var matchedIds = new HashSet<string>();

        // known Steam users first (sorted: MostRecent on top)
        foreach (var (id, name, recent) in steamUsers.OrderByDescending(u => u.MostRecent))
        {
            var hasSave = existingIds.Contains(id);
            accounts.Add(new SteamAccount(id, name, recent, hasSave));
            matchedIds.Add(id);
        }

        // include orphan folders (save exists but no matching user in loginusers.vdf)
        foreach (var id in existingIds.Where(id => !matchedIds.Contains(id)))
        {
            accounts.Add(new SteamAccount(id, id, false, true));
        }

        return new SavePathResult(basePath, accounts, null);
    }

    // ── private helpers ──────────────────────────────────────────

    private static string? GetSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var val = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(val))
            {
                // steam stores paths with forward slashes, normalize
                val = val.Replace('/', '\\');
                if (Directory.Exists(val)) return val;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to read Steam registry: {ex.Message}");
        }
        return null;
    }

    private static List<string> GetLibraryPaths(string steamPath)
    {
        var paths = new List<string>();
        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return paths;

        try
        {
            var content = File.ReadAllText(vdfPath);
            var root = ParseVdf(content);

            if (root.TryGetValue("libraryfolders", out var lfObj)
                && lfObj is Dictionary<string, object> lf)
            {
                foreach (var entry in lf.Values)
                {
                    if (entry is Dictionary<string, object> lib
                        && lib.TryGetValue("path", out var pathObj)
                        && pathObj is string libPath)
                    {
                        libPath = Path.GetFullPath(libPath.Replace('/', '\\'));
                        if (Directory.Exists(libPath))
                            paths.Add(libPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to parse libraryfolders.vdf: {ex.Message}");
        }

        return paths;
    }

    private static string? GetInstallDir(string libraryPath)
    {
        var acfPath = Path.Combine(libraryPath, "steamapps", $"appmanifest_{StS2AppId}.acf");
        if (!File.Exists(acfPath)) return null;

        try
        {
            var content = File.ReadAllText(acfPath);
            var root = ParseVdf(content);

            if (root.TryGetValue("AppState", out var stateObj)
                && stateObj is Dictionary<string, object> state
                && state.TryGetValue("installdir", out var dirObj)
                && dirObj is string installDir
                && !string.IsNullOrWhiteSpace(installDir))
            {
                return installDir;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to parse appmanifest: {ex.Message}");
        }

        return null;
    }

    private static List<(string SteamId64, string PersonaName, bool MostRecent)>
        GetSteamUsers(string steamPath)
    {
        var users = new List<(string, string, bool)>();
        var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(vdfPath)) return users;

        try
        {
            var content = File.ReadAllText(vdfPath);
            var root = ParseVdf(content);

            if (root.TryGetValue("users", out var usersObj)
                && usersObj is Dictionary<string, object> usersDict)
            {
                foreach (var (steamId, value) in usersDict)
                {
                    if (value is not Dictionary<string, object> info) continue;

                    var name = info.TryGetValue("PersonaName", out var n)
                        ? n as string ?? steamId : steamId;
                    var mostRecent = info.TryGetValue("MostRecent", out var mr)
                                     && mr is string mrStr && mrStr == "1";

                    users.Add((steamId, name, mostRecent));
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to parse loginusers.vdf: {ex.Message}");
        }

        return users;
    }

    // ── lightweight VDF parser ──────────────────────────────────

    /// <summary>
    /// Minimal VDF/ACF parser. Handles "key" "value" and "key" { ... } structure.
    /// </summary>
    private static Dictionary<string, object> ParseVdf(string content)
    {
        var tokens = Tokenize(content);
        var pos = 0;
        return ParseSection(tokens, ref pos);
    }

    private static List<string> Tokenize(string content)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c == '"')
            {
                var end = content.IndexOf('"', i + 1);
                if (end == -1) break;
                // handle escaped quotes (rare in VDF but just in case)
                while (end > 0 && content[end - 1] == '\\')
                    end = content.IndexOf('"', end + 1);
                if (end == -1) break;
                tokens.Add(content[(i + 1)..end]);
                i = end + 1;
            }
            else if (c is '{' or '}')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                // line comment
                var nl = content.IndexOf('\n', i);
                i = nl == -1 ? content.Length : nl + 1;
            }
            else
            {
                i++;
            }
        }
        return tokens;
    }

    private static Dictionary<string, object> ParseSection(List<string> tokens, ref int pos)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (pos < tokens.Count)
        {
            var token = tokens[pos];
            if (token == "}") { pos++; return dict; }

            var key = token;
            pos++;
            if (pos >= tokens.Count) break;

            if (tokens[pos] == "{")
            {
                pos++;
                dict[key] = ParseSection(tokens, ref pos);
            }
            else
            {
                dict[key] = tokens[pos];
                pos++;
            }
        }
        return dict;
    }
}
