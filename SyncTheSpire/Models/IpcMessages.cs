using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyncTheSpire.Models;

// -- request from frontend --

public class IpcRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }
}

// -- response to frontend --

public class IpcResponse
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    public static IpcResponse Success(string evt, object? data = null) =>
        new() { Event = evt, Data = new { status = "success", payload = data } };

    public static IpcResponse Error(string evt, string message) =>
        new() { Event = evt, Data = new { status = "error", message } };

    public static IpcResponse Progress(string evt, string message, int? percent = null, string? detail = null) =>
        new() { Event = evt, Data = new { status = "progress", message, percent, detail } };

    public static IpcResponse Conflict(string evt, object? data = null) =>
        new() { Event = evt, Data = new { status = "conflict", payload = data } };

    // reflection-based serialization -- source-gen can't handle anonymous types in Data
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializeOpts);
}
