using System.Text.Json.Serialization;

namespace SyncTheSpire.Models;

public class AppConfigV2
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("activeWorkspace")]
    public string? ActiveWorkspace { get; set; }

    [JsonPropertyName("openTabs")]
    public List<string> OpenTabs { get; set; } = [];

    [JsonPropertyName("workspaces")]
    public List<WorkspaceConfig> Workspaces { get; set; } = [];

    [JsonPropertyName("settings")]
    public AppSettings Settings { get; set; } = new();
}

public class AppSettings
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh-CN";
}
