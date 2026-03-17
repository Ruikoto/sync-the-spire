using System.Text.Json.Serialization;

namespace SyncTheSpire.Models;

public class AppConfig
{
    public string RepoUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// the actual game mod folder, e.g. "D:\SteamLibrary\steamapps\common\SlayTheSpire2\Mods"
    /// </summary>
    public string GameModPath { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(RepoUrl) &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Token) &&
        !string.IsNullOrWhiteSpace(GameModPath);
}
