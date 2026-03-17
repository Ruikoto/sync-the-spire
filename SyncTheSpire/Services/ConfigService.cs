using System.Text.Json;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

public class ConfigService
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncTheSpire");

    private static readonly string ConfigFilePath = Path.Combine(AppDataDir, "config.json");

    private AppConfig? _cached;

    public string RepoPath => Path.Combine(AppDataDir, "Repo");

    public ConfigService()
    {
        Directory.CreateDirectory(AppDataDir);
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
