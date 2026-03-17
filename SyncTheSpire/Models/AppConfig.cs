using System.Text.Json.Serialization;

namespace SyncTheSpire.Models;

public class AppConfig
{
    [JsonPropertyName("repoUrl")]
    public string RepoUrl { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("authType")]
    public string AuthType { get; set; } = "https"; // "https" | "ssh"

    [JsonPropertyName("sshKeyPath")]
    public string SshKeyPath { get; set; } = string.Empty;

    [JsonPropertyName("sshPassphrase")]
    public string SshPassphrase { get; set; } = string.Empty;

    /// <summary>
    /// the actual game mod folder, e.g. "D:\SteamLibrary\steamapps\common\SlayTheSpire2\Mods"
    /// </summary>
    [JsonPropertyName("gameModPath")]
    public string GameModPath { get; set; } = string.Empty;

    [JsonPropertyName("saveFolderPath")]
    public string SaveFolderPath { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(RepoUrl) &&
        !string.IsNullOrWhiteSpace(GameModPath) &&
        (AuthType == "ssh"
            ? !string.IsNullOrWhiteSpace(SshKeyPath)
            : !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Token));
}
