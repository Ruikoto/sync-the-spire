using Microsoft.Win32;
using SyncTheSpire.Helpers;
using SyncTheSpire.Services;

namespace SyncTheSpire.Adapters;

/// <summary>
/// Slay the Spire 2 adapter — Steam-based discovery, ModProfileBypass redirect,
/// 3-slot save profiles with modded/ subfolder, .json mod scanning.
/// </summary>
public class StS2Adapter : IGameAdapter
{
    private const int AppId = 2868840;
    private const string GameExeName = "SlayTheSpire2.exe";
    private const string RedirectModId = "ModProfileBypass";
    private static readonly string[] RedirectModFiles = ["ModProfileBypass.dll", "ModProfileBypass.json"];

    public string TypeKey => "sts2";
    public string DisplayName => "Slay the Spire 2";

    // ── path resolution ──────────────────────────────────────────────────

    public string? ResolveModPath(string gameInstallPath) =>
        string.IsNullOrWhiteSpace(gameInstallPath) ? null : Path.Combine(gameInstallPath, "Mods");

    public (string? Path, string? Error) ValidateGameInstallPath(string path)
    {
        // walk up to find the directory containing the game exe
        var resolved = FileSystemHelper.FindAncestorContaining(path, GameExeName, isFile: true);
        if (resolved is null)
            return (null, $"游戏安装路径无效：未找到 {GameExeName}\n请确认路径是否正确：{path}");
        return (resolved, null);
    }

    public (string? Path, string? Error) ValidateSaveFolderPath(string path)
    {
        // walk up to find the save root (must contain profile1/ and profile.save)
        var resolved = FileSystemHelper.FindAncestorContaining(path,
            dir => Directory.Exists(Path.Combine(dir, "profile1"))
                && File.Exists(Path.Combine(dir, "profile.save")));
        if (resolved is null)
            return (null, $"存档路径无效：未找到 profile1 文件夹或 profile.save 文件\n请确认路径是否正确：{path}");
        return (resolved, null);
    }

    // ── auto-discovery ───────────────────────────────────────────────────

    public bool SupportsAutoFind => true;

    public (string? Path, string? Error) FindGamePath()
    {
        var steamPath = GetSteamPath();
        if (steamPath is null)
            return (null, "未检测到 Steam 安装，请确认 Steam 已安装");

        var libraries = GetLibraryPaths(steamPath);
        if (libraries.Count == 0)
            return (null, "无法读取 Steam 库信息");

        foreach (var lib in libraries)
        {
            var installDir = GetInstallDir(lib);
            if (installDir is null) continue;

            var fullPath = Path.Combine(lib, "steamapps", "common", installDir);
            if (Directory.Exists(fullPath))
                return (fullPath, null);

            return (null, "检测到游戏安装记录，但安装目录不存在");
        }

        return (null, "未在 Steam 库中找到 Slay the Spire 2");
    }

    public SaveDiscoveryResult FindSavePath()
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2", "steam");

        if (!Directory.Exists(basePath))
            return new(null, null, null, "未找到存档目录，请确认游戏已运行过至少一次");

        var existingIds = new HashSet<string>(
            Directory.GetDirectories(basePath)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.All(char.IsDigit) && n.Length >= 10)!);

        if (existingIds.Count == 0)
            return new(null, null, null, "未找到任何存档文件夹");

        var steamPath = GetSteamPath();
        var steamUsers = steamPath is not null ? GetSteamUsers(steamPath) : [];

        var accounts = new List<SaveAccountInfo>();
        var matchedIds = new HashSet<string>();

        foreach (var (id, name, recent) in steamUsers.OrderByDescending(u => u.MostRecent))
        {
            var hasSave = existingIds.Contains(id);
            accounts.Add(new SaveAccountInfo(id, name, recent, hasSave));
            matchedIds.Add(id);
        }

        foreach (var id in existingIds.Where(id => !matchedIds.Contains(id)))
            accounts.Add(new SaveAccountInfo(id, id, false, true));

        return new(basePath, accounts, null, null);
    }

    // ── capabilities ─────────────────────────────────────────────────────

    public bool SupportsSaveRedirect => true;
    public bool SupportsSaveBackup => true;
    public bool SupportsModdedSaves => true;
    public bool SupportsModScanning => true;
    public bool SupportsJunction => true;
    public bool ComingSoon => false;

    // ── save redirect (ModProfileBypass) ─────────────────────────────────

    public bool IsSaveRedirectEnabled(string gameModPath)
    {
        var modDir = Path.Combine(gameModPath, RedirectModId);
        return RedirectModFiles.All(f => File.Exists(Path.Combine(modDir, f)));
    }

    public void EnableSaveRedirect(string gameModPath)
    {
        var assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", RedirectModId);
        if (!Directory.Exists(assetsDir))
            throw new InvalidOperationException("重定向 Mod 资源缺失，请重新安装软件");

        var modDir = Path.Combine(gameModPath, RedirectModId);
        Directory.CreateDirectory(modDir);
        foreach (var file in RedirectModFiles)
            File.Copy(Path.Combine(assetsDir, file), Path.Combine(modDir, file), overwrite: true);
    }

    public void DisableSaveRedirect(string gameModPath)
    {
        var modDir = Path.Combine(gameModPath, RedirectModId);
        foreach (var file in RedirectModFiles)
        {
            var path = Path.Combine(modDir, file);
            if (File.Exists(path))
                File.Delete(path);
        }

        // clean up empty directory
        if (Directory.Exists(modDir) && !Directory.EnumerateFileSystemEntries(modDir).Any())
            Directory.Delete(modDir);
    }

    // ── save structure ───────────────────────────────────────────────────

    public string[] SaveProfileNames => ["profile1", "profile2", "profile3"];
    public string ModdedSaveSubfolder => "modded";

    // ── Steam helpers (moved from SteamFinderService) ────────────────────

    private static string? GetSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var val = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(val))
            {
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
            var root = VdfParser.Parse(content);

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

    private string? GetInstallDir(string libraryPath)
    {
        var acfPath = Path.Combine(libraryPath, "steamapps", $"appmanifest_{AppId}.acf");
        if (!File.Exists(acfPath)) return null;

        try
        {
            var content = File.ReadAllText(acfPath);
            var root = VdfParser.Parse(content);

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
            var root = VdfParser.Parse(content);

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
}
