namespace SyncTheSpire.Adapters;

/// <summary>
/// Minecraft adapter — placeholder for future game-specific logic.
/// Currently behaves identically to GenericAdapter. ComingSoon = true
/// so it's disabled in release builds until full support is ready.
/// </summary>
public class MinecraftAdapter : IGameAdapter
{
    public string TypeKey => "minecraft";
    public string DisplayName => "Minecraft";

    // ── path resolution ──────────────────────────────────────────────────

    public string? ResolveModPath(string gameInstallPath) =>
        string.IsNullOrWhiteSpace(gameInstallPath) ? null : gameInstallPath;

    public (string? Path, string? Error) ValidateGameInstallPath(string path) =>
        Directory.Exists(path) ? (path, null) : (null, $"路径不存在：{path}");

    public (string? Path, string? Error) ValidateSaveFolderPath(string path) =>
        Directory.Exists(path) ? (path, null) : (null, $"路径不存在：{path}");

    // ── auto-discovery ───────────────────────────────────────────────────

    public bool SupportsAutoFind => false;

    public (string? Path, string? Error) FindGamePath() =>
        (null, "Minecraft 暂不支持自动检测");

    public SaveDiscoveryResult FindSavePath() =>
        new(null, null, null, "Minecraft 暂不支持自动检测");

    // ── capabilities ─────────────────────────────────────────────────────

    public bool SupportsSaveRedirect => false;
    public bool SupportsSaveBackup => false;
    public bool SupportsModdedSaves => false;
    public bool SupportsModScanning => false;
    public bool ComingSoon => true;

    // ── save redirect (no-op) ────────────────────────────────────────────

    public bool IsSaveRedirectEnabled(string gameModPath) => false;
    public void EnableSaveRedirect(string gameModPath) { }
    public void DisableSaveRedirect(string gameModPath) { }

    // ── save structure ───────────────────────────────────────────────────

    public string[] SaveProfileNames => [];
    public string ModdedSaveSubfolder => string.Empty;

}
