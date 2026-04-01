using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

/// <summary>
/// Central manager for multi-workspace config (v2).
/// Handles persistence, DPAPI encryption, V1→V2 migration, workspace CRUD, and path resolution.
/// </summary>
public class WorkspaceManager
{
    public static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncTheSpire");

    private static readonly string ConfigFilePath = Path.Combine(AppDataDir, "config.json");
    private static readonly string DismissalsFilePath = Path.Combine(AppDataDir, "dismissals.json");
    private static readonly string WorkspacesRoot = Path.Combine(AppDataDir, "workspaces");

    private const string EncPrefix = "dpapi:";

    private AppConfigV2 _config = null!;

    // fallback for migration when Directory.Move fails (files locked, etc.)
    private readonly Dictionary<string, (string RepoPath, string GitDirPath)> _pathOverrides = new();

    public AppConfigV2 Config => _config;
    public string DismissalsPath => DismissalsFilePath;

    public WorkspaceManager()
    {
        // if Store version previously wrote data to the MSIX-virtualized path, move it to the real one
        if (DistributionHelper.IsMsixPackaged && !Directory.Exists(AppDataDir))
            TryMigrateFromVirtualizedPath();

        Directory.CreateDirectory(AppDataDir);
        LoadConfig();
    }

    // ── config persistence ──────────────────────────────────────────────

    private void LoadConfig()
    {
        if (!File.Exists(ConfigFilePath))
        {
            _config = new AppConfigV2();
            return;
        }

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // detect v1 config: no "version" field
            if (!root.TryGetProperty("version", out _))
            {
                MigrateFromV1(json);
                return;
            }

            _config = JsonSerializer.Deserialize<AppConfigV2>(json) ?? new AppConfigV2();
        }
        catch (Exception ex)
        {
            LogService.Warn($"Config file corrupted, using defaults: {ex.Message}");
            _config = new AppConfigV2();
        }

        // decrypt secrets for all workspaces
        foreach (var ws in _config.Workspaces)
        {
            ws.Token = DpapiDecrypt(ws.Token);
            ws.SshPassphrase = DpapiDecrypt(ws.SshPassphrase);
        }
    }

    public void SaveConfig()
    {
        // build a serialization copy with encrypted secrets
        var toSerialize = new AppConfigV2
        {
            Version = _config.Version,
            ActiveWorkspace = _config.ActiveWorkspace,
            OpenTabs = [.._config.OpenTabs],
            Settings = _config.Settings,
            Workspaces = _config.Workspaces.Select(ws => new WorkspaceConfig
            {
                Id = ws.Id,
                Name = ws.Name,
                GameType = ws.GameType,
                RepoUrl = ws.RepoUrl,
                Nickname = ws.Nickname,
                Username = ws.Username,
                Token = DpapiEncrypt(ws.Token),
                AuthType = ws.AuthType,
                SshKeyPath = ws.SshKeyPath,
                SshPassphrase = DpapiEncrypt(ws.SshPassphrase),
                GameInstallPath = ws.GameInstallPath,
                GameModPathLegacy = ws.GameModPathLegacy,
                SaveFolderPath = ws.SaveFolderPath,
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(toSerialize, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFilePath, json);
        LogService.Info("Config saved (v2)");
    }

    // ── V1 → V2 migration ──────────────────────────────────────────────

    private void MigrateFromV1(string v1Json)
    {
        LogService.Info("Detected v1 config, starting migration to v2...");

        // 1. backup the old config
        var backupPath = Path.Combine(AppDataDir, "config.v1.backup.json");
        try { File.Copy(ConfigFilePath, backupPath, overwrite: true); }
        catch (Exception ex) { LogService.Warn($"Failed to backup v1 config: {ex.Message}"); }

        // 2. deserialize the old config
        AppConfig? v1;
        try
        {
            v1 = JsonSerializer.Deserialize<AppConfig>(v1Json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to deserialize v1 config: {ex.Message}");
            _config = new AppConfigV2();
            return;
        }

        // decrypt v1 secrets
        v1.Token = DpapiDecrypt(v1.Token);
        v1.SshPassphrase = DpapiDecrypt(v1.SshPassphrase);

        // 3. create workspace config from v1
        var wsId = Guid.NewGuid().ToString();
        var ws = new WorkspaceConfig
        {
            Id = wsId,
            Name = "Slay the Spire 2",
            GameType = "sts2",
            RepoUrl = v1.RepoUrl,
            Nickname = v1.Nickname,
            Username = v1.Username,
            Token = v1.Token,
            AuthType = v1.AuthType,
            SshKeyPath = v1.SshKeyPath,
            SshPassphrase = v1.SshPassphrase,
            GameInstallPath = v1.GameInstallPath,
            GameModPathLegacy = v1.GameModPathLegacy,
            SaveFolderPath = v1.SaveFolderPath,
        };

        // 4. attempt to move data directories into workspace folder
        var wsDir = Path.Combine(WorkspacesRoot, wsId);
        Directory.CreateDirectory(wsDir);

        var oldRepo = Path.Combine(AppDataDir, "Repo");
        var oldGitDir = Path.Combine(AppDataDir, "GitDir");
        var oldBackups = Path.Combine(AppDataDir, "Backups");

        var newRepo = Path.Combine(wsDir, "Repo");
        var newGitDir = Path.Combine(wsDir, "GitDir");
        var newBackups = Path.Combine(wsDir, "Backups");

        // if a junction exists at GameModPath pointing to old Repo, remove it before moving
        var junctionService = new JunctionService();
        if (!string.IsNullOrWhiteSpace(ws.GameModPath) && junctionService.IsJunction(ws.GameModPath))
        {
            try
            {
                junctionService.RemoveJunction(ws.GameModPath);
                LogService.Info($"Removed old junction at {ws.GameModPath} before migration");
            }
            catch (Exception ex) { LogService.Warn($"Failed to remove old junction: {ex.Message}"); }
        }

        MoveDirectorySafe(oldRepo, newRepo);
        MoveDirectorySafe(oldGitDir, newGitDir);
        MoveDirectorySafe(oldBackups, newBackups);

        // if move failed (files locked, etc.), data stayed at old location —
        // override workspace path resolution to point at old paths so the app still works
        var actualRepo = Directory.Exists(newRepo) ? newRepo : (Directory.Exists(oldRepo) ? oldRepo : newRepo);
        var actualGitDir = Directory.Exists(newGitDir) ? newGitDir : (Directory.Exists(oldGitDir) ? oldGitDir : newGitDir);

        // patch core.worktree in git config — after moving dirs the old path is stale
        PatchGitCoreWorktree(actualGitDir, actualRepo);

        // recreate junction at GameModPath -> actual Repo path
        if (!string.IsNullOrWhiteSpace(ws.GameModPath) && Directory.Exists(actualRepo))
        {
            try
            {
                junctionService.CreateJunction(ws.GameModPath, actualRepo);
                LogService.Info($"Recreated junction at {ws.GameModPath} -> {actualRepo}");
            }
            catch (Exception ex) { LogService.Warn($"Failed to recreate junction: {ex.Message}"); }
        }

        // 5. assemble v2 config
        _config = new AppConfigV2
        {
            Version = 2,
            ActiveWorkspace = wsId,
            OpenTabs = [wsId],
            Workspaces = [ws],
            Settings = new AppSettings(),
        };

        // store override paths if moves failed (will be used by GetRepoPath/GetGitDirPath)
        if (actualRepo != newRepo || actualGitDir != newGitDir)
        {
            _pathOverrides[wsId] = (actualRepo, actualGitDir);
            LogService.Info($"Using fallback paths for workspace {wsId}: Repo={actualRepo}, GitDir={actualGitDir}");
        }

        SaveConfig();
        LogService.Info($"V1 → V2 migration completed. Workspace ID: {wsId}");
    }

    private static void MoveDirectorySafe(string source, string dest)
    {
        if (!Directory.Exists(source)) return;
        try
        {
            Directory.Move(source, dest);
            LogService.Info($"Moved {source} → {dest}");
        }
        catch (Exception ex)
        {
            // files might be locked — log and leave in old location
            LogService.Warn($"Failed to move {source} → {dest}: {ex.Message}");
        }
    }

    /// <summary>
    /// update core.worktree in the git config to match the actual repo path after migration
    /// </summary>
    private static void PatchGitCoreWorktree(string gitDirPath, string repoPath)
    {
        var gitConfigPath = Path.Combine(gitDirPath, "config");
        if (!File.Exists(gitConfigPath)) return;

        try
        {
            var content = File.ReadAllText(gitConfigPath);
            // git config stores worktree as: worktree = C:/path/to/repo (forward slashes)
            var normalized = repoPath.Replace('\\', '/');

            // replace existing worktree line with updated path
            var patched = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"(?m)^(\s*worktree\s*=\s*).*$",
                $"${{1}}{normalized}");

            if (patched != content)
            {
                File.WriteAllText(gitConfigPath, patched);
                LogService.Info($"Patched core.worktree → {normalized}");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to patch core.worktree: {ex.Message}");
        }
    }

    // ── workspace CRUD ──────────────────────────────────────────────────

    public WorkspaceConfig CreateWorkspace(string name, string gameType)
    {
        var ws = new WorkspaceConfig
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            GameType = gameType,
        };

        _config.Workspaces.Add(ws);
        // ensure the workspace data dir exists
        Directory.CreateDirectory(GetWorkspaceDir(ws.Id));
        SaveConfig();
        return ws;
    }

    public void DeleteWorkspace(string id)
    {
        var ws = _config.Workspaces.FirstOrDefault(w => w.Id == id);
        if (ws == null) return;

        // clean up data directory
        var wsDir = GetWorkspaceDir(id);

        // remove junction first if exists, so we don't accidentally nuke the game mod folder
        if (!string.IsNullOrWhiteSpace(ws.GameModPath))
        {
            var junctionService = new JunctionService();
            if (junctionService.IsJunction(ws.GameModPath))
            {
                try { junctionService.RemoveJunction(ws.GameModPath); }
                catch (Exception ex) { LogService.Warn($"Failed to remove junction on workspace delete: {ex.Message}"); }
            }
        }

        if (Directory.Exists(wsDir))
        {
            try { Directory.Delete(wsDir, true); }
            catch (Exception ex) { LogService.Warn($"Failed to delete workspace dir: {ex.Message}"); }
        }

        _config.Workspaces.Remove(ws);
        _config.OpenTabs.Remove(id);
        if (_config.ActiveWorkspace == id)
            _config.ActiveWorkspace = _config.OpenTabs.FirstOrDefault();

        SaveConfig();
    }

    public WorkspaceConfig? GetWorkspace(string id) =>
        _config.Workspaces.FirstOrDefault(w => w.Id == id);

    public IReadOnlyList<WorkspaceConfig> GetAllWorkspaces() => _config.Workspaces;

    public void UpdateWorkspace(WorkspaceConfig updated)
    {
        var idx = _config.Workspaces.FindIndex(w => w.Id == updated.Id);
        if (idx >= 0)
            _config.Workspaces[idx] = updated;
        SaveConfig();
    }

    // ── tab management ──────────────────────────────────────────────────

    public void SetActiveWorkspace(string id)
    {
        _config.ActiveWorkspace = id;
        if (!_config.OpenTabs.Contains(id))
            _config.OpenTabs.Add(id);
        SaveConfig();
    }

    public void OpenTab(string id)
    {
        if (!_config.OpenTabs.Contains(id))
        {
            _config.OpenTabs.Add(id);
            SaveConfig();
        }
    }

    public void CloseTab(string id)
    {
        _config.OpenTabs.Remove(id);
        if (_config.ActiveWorkspace == id)
            _config.ActiveWorkspace = _config.OpenTabs.FirstOrDefault();
        SaveConfig();
    }

    // ── path resolution ─────────────────────────────────────────────────

    public string GetWorkspaceDir(string id) => Path.Combine(WorkspacesRoot, id);

    public string GetRepoPath(string id) =>
        _pathOverrides.TryGetValue(id, out var o) ? o.RepoPath : Path.Combine(GetWorkspaceDir(id), "Repo");

    public string GetGitDirPath(string id) =>
        _pathOverrides.TryGetValue(id, out var o) ? o.GitDirPath : Path.Combine(GetWorkspaceDir(id), "GitDir");

    public string GetBackupDir(string id) => Path.Combine(GetWorkspaceDir(id), "Backups");

    // ── dismissed announcements (global) ────────────────────────────────

    public List<string> GetDismissedAnnouncements()
    {
        try
        {
            if (!File.Exists(DismissalsFilePath)) return [];
            var json = File.ReadAllText(DismissalsFilePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch { return []; }
    }

    public void DismissAnnouncement(string id)
    {
        var list = GetDismissedAnnouncements();
        if (list.Contains(id)) return;
        list.Add(id);
        File.WriteAllText(DismissalsFilePath, JsonSerializer.Serialize(list));
    }

    // ── MSIX virtualized-path migration ─────────────────────────────────

    private static void TryMigrateFromVirtualizedPath()
    {
        try
        {
            var localCache = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
            var virtualizedPath = Path.Combine(localCache, "Local", "SyncTheSpire");

            if (!Directory.Exists(virtualizedPath)) return;

            Directory.Move(virtualizedPath, AppDataDir);
            LogService.Info($"Migrated data from virtualized MSIX path: {virtualizedPath}");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Virtualized path migration failed: {ex.Message}");
        }
    }

    // ── DPAPI helpers ───────────────────────────────────────────────────

    // exposed as internal static for ConfigService backward compat during transition
    internal static string DpapiEncrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return EncPrefix + Convert.ToBase64String(encrypted);
    }

    internal static string DpapiDecrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;

        if (stored.StartsWith(EncPrefix))
        {
            try
            {
                var encrypted = Convert.FromBase64String(stored[EncPrefix.Length..]);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                LogService.Warn($"DPAPI decrypt failed: {ex.Message}");
                return string.Empty;
            }
        }

        // no prefix — plaintext from older config
        return stored;
    }
}
