using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

public class ConfigService
{
    public static readonly string AppDataDirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncTheSpire");

    private static readonly string ConfigFilePath = Path.Combine(AppDataDirPath, "config.json");

    // prefix to distinguish DPAPI-encrypted values from plaintext in the JSON
    private const string EncPrefix = "dpapi:";

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

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            _cached = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            // corrupted config file — fall back to defaults so the app doesn't crash
            _cached = new AppConfig();
        }

        // decrypt sensitive fields (backward-compatible with plaintext from older versions)
        _cached.Token = DpapiDecrypt(_cached.Token);
        _cached.SshPassphrase = DpapiDecrypt(_cached.SshPassphrase);

        return _cached;
    }

    public void SaveConfig(AppConfig config)
    {
        _cached = config;

        // write a copy with encrypted sensitive fields
        var toSerialize = new AppConfig
        {
            RepoUrl = config.RepoUrl,
            Username = config.Username,
            Token = DpapiEncrypt(config.Token),
            AuthType = config.AuthType,
            SshKeyPath = config.SshKeyPath,
            SshPassphrase = DpapiEncrypt(config.SshPassphrase),
            GameInstallPath = config.GameInstallPath,
            GameModPathLegacy = config.GameModPathLegacy,
            SaveFolderPath = config.SaveFolderPath,
        };

        var json = JsonSerializer.Serialize(toSerialize, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFilePath, json);
    }

    /// <summary>
    /// blow away cached config so next LoadConfig re-reads from disk
    /// </summary>
    public void InvalidateCache() => _cached = null;

    // ── DPAPI helpers ────────────────────────────────────────────────

    private static string DpapiEncrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return EncPrefix + Convert.ToBase64String(encrypted);
    }

    private static string DpapiDecrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;

        // already encrypted with our prefix
        if (stored.StartsWith(EncPrefix))
        {
            try
            {
                var encrypted = Convert.FromBase64String(stored[EncPrefix.Length..]);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // corrupted — treat as empty
                return string.Empty;
            }
        }

        // no prefix — plaintext from an older config, return as-is (will be encrypted on next save)
        return stored;
    }
}
