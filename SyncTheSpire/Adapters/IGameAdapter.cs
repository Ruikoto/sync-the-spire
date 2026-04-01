namespace SyncTheSpire.Adapters;

/// <summary>
/// Abstracts game-specific behavior so the app core stays game-agnostic.
/// Each game type (sts2, generic, ...) provides one implementation.
/// </summary>
public interface IGameAdapter
{
    string TypeKey { get; }       // "sts2", "generic"
    string DisplayName { get; }   // shown in UI game-type selector

    // ── path resolution ──────────────────────────────────────────────────

    /// <summary>
    /// derive the mod/sync folder from the game install root.
    /// e.g. StS2: {installPath}\Mods. Generic: identity (user picks exact path).
    /// returns null if installPath is empty/whitespace.
    /// </summary>
    string? ResolveModPath(string gameInstallPath);

    /// <summary>
    /// validate + auto-correct game install path (walk up ancestors if needed).
    /// returns (resolvedPath, null) on success, (null, errorMessage) on failure.
    /// </summary>
    (string? Path, string? Error) ValidateGameInstallPath(string path);

    /// <summary>
    /// validate + auto-correct save folder path.
    /// returns (resolvedPath, null) on success, (null, errorMessage) on failure.
    /// </summary>
    (string? Path, string? Error) ValidateSaveFolderPath(string path);

    // ── auto-discovery (Steam, etc.) ─────────────────────────────────────

    bool SupportsAutoFind { get; }

    /// <summary>
    /// try to locate the game install directory automatically.
    /// returns (path, null) on success, (null, errorMessage) on failure.
    /// </summary>
    (string? Path, string? Error) FindGamePath();

    /// <summary>
    /// try to discover save folder(s).
    /// returns structured account data for StS2, or a single path for simpler games.
    /// </summary>
    SaveDiscoveryResult FindSavePath();

    // ── capabilities (controls which UI features are shown) ──────────────

    bool SupportsSaveRedirect { get; }
    bool SupportsSaveBackup { get; }
    bool SupportsModdedSaves { get; }
    bool SupportsModScanning { get; }

    // ── save redirect (ModProfileBypass for StS2, no-op for others) ──────

    /// <summary>
    /// check if save redirect is currently enabled (mod files present, etc.)
    /// </summary>
    bool IsSaveRedirectEnabled(string gameModPath);

    /// <summary>
    /// enable save redirect (copy mod files, toggle config, etc.)
    /// throws on failure.
    /// </summary>
    void EnableSaveRedirect(string gameModPath);

    /// <summary>
    /// disable save redirect.
    /// throws on failure.
    /// </summary>
    void DisableSaveRedirect(string gameModPath);

    // ── save structure ───────────────────────────────────────────────────

    /// <summary>
    /// profile slot names used by this game. e.g. ["profile1","profile2","profile3"] for StS2.
    /// empty for games without fixed profiles.
    /// </summary>
    string[] SaveProfileNames { get; }

    /// <summary>
    /// subfolder under the save root that holds modded copies. e.g. "modded" for StS2.
    /// empty string if not applicable.
    /// </summary>
    string ModdedSaveSubfolder { get; }

    // ── git exclude rules (game-specific ignores) ────────────────────────

    /// <summary>
    /// extra lines to append to info/exclude beyond the default OS/IDE rules.
    /// return empty if no game-specific exclusions needed.
    /// </summary>
    IReadOnlyList<string> GetExcludeRules();
}

// ── shared result types ──────────────────────────────────────────────────

public record SaveDiscoveryResult(
    string? BasePath,
    List<SaveAccountInfo>? Accounts,
    string? SinglePath,
    string? Error);

public record SaveAccountInfo(
    string SteamId64,
    string PersonaName,
    bool MostRecent,
    bool HasSaveFolder);
