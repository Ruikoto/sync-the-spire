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
}
