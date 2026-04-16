namespace SyncTheSpire.Adapters;

/// <summary>
/// Maps game type keys to adapter instances.
/// </summary>
public static class GameAdapterRegistry
{
    private static readonly Dictionary<string, IGameAdapter> Adapters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sts2"] = new StS2Adapter(),
        ["stardew"] = new StardewValleyAdapter(),
        ["minecraft"] = new MinecraftAdapter(),
        ["generic"] = new GenericAdapter(),
    };

    public static IGameAdapter Get(string typeKey) =>
        Adapters.TryGetValue(typeKey, out var adapter)
            ? adapter
            : Adapters["generic"]; // fallback to generic for unknown types

    public static IReadOnlyList<IGameAdapter> All => [.. Adapters.Values];
}
