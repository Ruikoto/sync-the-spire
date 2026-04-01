namespace SyncTheSpire.Adapters;

/// <summary>
/// Generic git-sync adapter — no game-specific logic.
/// Users pick an arbitrary folder to sync via git. No auto-find, no save redirect,
/// no modded saves, no mod scanning.
/// </summary>
public class GenericAdapter : IGameAdapter
{
    public string TypeKey => "generic";
    public string DisplayName => "通用";

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
        (null, "通用模式不支持自动检测");

    public SaveDiscoveryResult FindSavePath() =>
        new(null, null, null, "通用模式不支持自动检测");

    // ── capabilities ─────────────────────────────────────────────────────

    public bool SupportsSaveRedirect => false;
    public bool SupportsSaveBackup => false;
    public bool SupportsModdedSaves => false;
    public bool SupportsModScanning => false;

    // ── save redirect (no-op) ────────────────────────────────────────────

    public bool IsSaveRedirectEnabled(string gameModPath) => false;
    public void EnableSaveRedirect(string gameModPath) { }
    public void DisableSaveRedirect(string gameModPath) { }

    // ── save structure ───────────────────────────────────────────────────

    public string[] SaveProfileNames => [];
    public string ModdedSaveSubfolder => string.Empty;

}
