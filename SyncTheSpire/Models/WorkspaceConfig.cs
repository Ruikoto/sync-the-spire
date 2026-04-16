using System.Text.Json.Serialization;

namespace SyncTheSpire.Models;

public class WorkspaceConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("gameType")]
    public string GameType { get; set; } = "sts2"; // "sts2" | "generic"

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
    /// the actual mod folder is {GameInstallPath}\Mods for StS2
    /// </summary>
    [JsonPropertyName("gameInstallPath")]
    public string GameInstallPath { get; set; } = string.Empty;

    // keep reading old configs that used "gameModPath"
    [JsonPropertyName("gameModPath")]
    public string GameModPathLegacy { get; set; } = string.Empty;

    [JsonPropertyName("saveFolderPath")]
    public string SaveFolderPath { get; set; } = string.Empty;

    // fallback paths for V1→V2 migration when Directory.Move fails (files locked)
    // if non-empty, these override the default workspaces/{id}/Repo|GitDir paths
    [JsonPropertyName("repoPathOverride")]
    public string RepoPathOverride { get; set; } = string.Empty;

    [JsonPropertyName("gitDirPathOverride")]
    public string GitDirPathOverride { get; set; } = string.Empty;

    [JsonPropertyName("customExePath")]
    public string CustomExePath { get; set; } = string.Empty;

    /// <summary>
    /// resolved mod/sync folder: StS2 = {GameInstallPath}\Mods, generic = GameInstallPath itself
    /// </summary>
    [JsonIgnore]
    public string GameModPath =>
        !string.IsNullOrWhiteSpace(GameInstallPath)
            ? GameType == "generic" ? GameInstallPath : Path.Combine(GameInstallPath, "Mods")
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
