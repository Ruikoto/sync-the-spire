using System.Text.Json;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

public class ConfigService
{
    public static readonly string AppDataDirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncTheSpire");

    private static readonly string ConfigFilePath = Path.Combine(AppDataDirPath, "config.json");

    private AppConfig? _cached;

    public string RepoPath => Path.Combine(AppDataDirPath, "Repo");

    // separated git dir -- keeps .git out of the working tree (and the junction)
    public string GitDirPath => Path.Combine(AppDataDirPath, "GitDir");

    public bool IsRepoInitialized => Directory.Exists(GitDirPath) &&
                                     Directory.Exists(Path.Combine(GitDirPath, "objects"));

    public ConfigService()
    {
        Directory.CreateDirectory(AppDataDirPath);
    }

    public AppConfig LoadConfig()
    {
        if (_cached is not null) return _cached;

        if (!File.Exists(ConfigFilePath))
        {
            _cached = new AppConfig();
            return _cached;
        }

        var json = File.ReadAllText(ConfigFilePath);
        _cached = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        return _cached;
    }

    public void SaveConfig(AppConfig config)
    {
        _cached = config;
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFilePath, json);
    }

    /// <summary>
    /// blow away cached config so next LoadConfig re-reads from disk
    /// </summary>
    public void InvalidateCache() => _cached = null;
}
