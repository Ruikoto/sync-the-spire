using System.Text.Json.Serialization;

namespace SyncTheSpire.Models;

/// <summary>
/// V1 config model — used ONLY for deserializing legacy v1 config.json during migration.
/// All runtime code should use WorkspaceConfig directly.
/// </summary>
[Obsolete("V1 migration only. Use WorkspaceConfig for all runtime code.")]
public class AppConfig
{
    [JsonPropertyName("repoUrl")]
    public string RepoUrl { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("authType")]
    public string AuthType { get; set; } = "anonymous"; // "anonymous" | "https" | "ssh"

    [JsonPropertyName("sshKeyPath")]
    public string SshKeyPath { get; set; } = string.Empty;

    [JsonPropertyName("sshPassphrase")]
    public string SshPassphrase { get; set; } = string.Empty;

    /// <summary>
    /// game install root, e.g. "D:\SteamLibrary\steamapps\common\SlayTheSpire2"
    /// the actual mod folder is {GameInstallPath}\Mods
    /// </summary>
    [JsonPropertyName("gameInstallPath")]
    public string GameInstallPath { get; set; } = string.Empty;

    // keep reading old configs that used "gameModPath"
    [JsonPropertyName("gameModPath")]
    public string GameModPathLegacy { get; set; } = string.Empty;

    [JsonPropertyName("saveFolderPath")]
    public string SaveFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// resolved mod folder: {GameInstallPath}\Mods
    /// </summary>
    [JsonIgnore]
    public string GameModPath =>
        !string.IsNullOrWhiteSpace(GameInstallPath)
            ? Path.Combine(GameInstallPath, "Mods")
            : GameModPathLegacy; // fallback for old configs

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Nickname) &&
        !string.IsNullOrWhiteSpace(RepoUrl) &&
        !string.IsNullOrWhiteSpace(GameModPath) &&
        AuthType switch
        {
            "ssh" => !string.IsNullOrWhiteSpace(SshKeyPath),
            "https" => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Token),
            "anonymous" => true,
            _ => false
        };
}
