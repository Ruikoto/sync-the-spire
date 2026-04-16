using System.Text.Json.Serialization;

namespace SyncTheSpire.Models;

// mod definition parsed from branch tree JSON files
public record ModInfo
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }

    // manifest fields for mod manager
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; init; } = [];
    [JsonPropertyName("has_dll")] public bool HasDll { get; init; }
    [JsonPropertyName("has_pck")] public bool HasPck { get; init; }

    // detailed scan fields (not from JSON — populated at runtime)
    [JsonIgnore] public string FolderName { get; init; } = "";
    [JsonIgnore] public string FolderPath { get; init; } = "";
    [JsonIgnore] public List<string> Files { get; init; } = [];
    [JsonIgnore] public long SizeBytes { get; init; }
    [JsonIgnore] public List<string> MissingFiles { get; init; } = [];
    [JsonIgnore] public List<string> DependedBy { get; init; } = [];
}
