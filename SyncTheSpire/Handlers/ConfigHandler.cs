using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Adapters;
using SyncTheSpire.Helpers;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class ConfigHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly GitService _gitService;
    private readonly JunctionService _junctionService;
    private readonly JunctionHelper _junctionHelper;
    private readonly IGameAdapter _adapter;

    public ConfigHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        GitService gitService,
        JunctionService junctionService,
        JunctionHelper junctionHelper,
        IGameAdapter adapter)
        : base(webView, uiContext)
    {
        _configService = configService;
        _gitService = gitService;
        _junctionService = junctionService;
        _junctionHelper = junctionHelper;
        _adapter = adapter;
    }

    public void HandleGetVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(); // x64, arm64, etc.
        var distribution = DistributionHelper.IsMsixPackaged ? "store" : "direct";
        Send(IpcResponse.Success("GET_VERSION", new { version, arch, distribution }));
    }

    public void HandleGetStatus()
    {
        var ws = _configService.Workspace;

        // adapter capabilities — frontend uses these to show/hide UI sections
        var capabilities = new
        {
            supportsSaveRedirect = _adapter.SupportsSaveRedirect,
            supportsSaveBackup = _adapter.SupportsSaveBackup,
            supportsModdedSaves = _adapter.SupportsModdedSaves,
            supportsModScanning = _adapter.SupportsModScanning,
            supportsAutoFind = _adapter.SupportsAutoFind,
            // generic adapter: junction is always on, no toggle needed
            supportsModToggle = _adapter.TypeKey != "generic",
        };

        // C3 fix: short-circuit when no real workspace context
        var repoExists = _gitService != null && _gitService.IsRepoValid;

        object data;
        if (!ws.IsConfigured || !repoExists)
        {
            data = new
            {
                isConfigured = false,
                currentBranch = (string?)null,
                isJunctionActive = false,
                hasLocalChanges = false,
                capabilities
            };
        }
        else
        {
            var isJunction = _junctionService.IsJunction(ws.GameModPath);
            var branch = _gitService!.GetCurrentBranch();
            var isInit = branch == GitService.InitBranch;
            data = new
            {
                isConfigured = true,
                currentBranch = isInit ? (string?)null : branch,
                isJunctionActive = isJunction,
                hasLocalChanges = isInit ? false : _gitService.HasLocalChanges(),
                needsBranchSelection = isInit,
                capabilities
            };
        }

        Send(IpcResponse.Success("GET_STATUS", data));
    }

    /// <summary>
    /// fetch from remote and return ahead/behind counts -- used by the refresh button
    /// </summary>
    public void HandleRefreshSync()
    {
        var ws = _configService.Workspace;
        if (!ws.IsConfigured || _gitService == null || !_gitService.IsRepoValid || _gitService.IsOnInitBranch)
        {
            // H3 fix: return REFRESH_SYNC event (not GET_STATUS) so frontend stops spinner
            Send(IpcResponse.Success("REFRESH_SYNC", new
            {
                currentBranch = (string?)null,
                isJunctionActive = false,
                hasLocalChanges = false,
                ahead = 0,
                behind = 0,
                hasRemoteBranch = false
            }));
            return;
        }

        var sync = _gitService.FetchAndGetSyncStatus();
        var isJunction = _junctionService.IsJunction(ws.GameModPath);
        var branch = _gitService.GetCurrentBranch();

        Send(IpcResponse.Success("REFRESH_SYNC", new
        {
            currentBranch = branch,
            isJunctionActive = isJunction,
            hasLocalChanges = _gitService.HasLocalChanges(),
            ahead = sync.Ahead,
            behind = sync.Behind,
            hasRemoteBranch = sync.HasRemoteBranch
        }));
    }

    /// <summary>
    /// return saved config to frontend for pre-filling the settings form
    /// strips sensitive fields (token, ssh passphrase)
    /// </summary>
    public void HandleGetConfig()
    {
        var cfg = _configService.LoadConfig();
        var gitUserName = _gitService?.ReadGitGlobalConfig("user.name");
        Send(IpcResponse.Success("GET_CONFIG", new
        {
            nickname = cfg.Nickname,
            repoUrl = cfg.RepoUrl,
            authType = cfg.AuthType,
            username = cfg.Username,
            sshKeyPath = cfg.SshKeyPath,
            gameInstallPath = cfg.GameInstallPath,
            saveFolderPath = cfg.SaveFolderPath,
            gitUserName,
            // don't return token or sshPassphrase
        }));
    }

    public void HandleInitConfig(JsonElement? payload)
    {
        if (payload is null)
        {
            Send(IpcResponse.Error("INIT_CONFIG", "Missing payload"));
            return;
        }

        var raw = payload.Value.GetRawText();
        var cfg = JsonSerializer.Deserialize<AppConfig>(raw);
        if (cfg is null)
        {
            Send(IpcResponse.Error("INIT_CONFIG", "请填写所有配置项"));
            return;
        }

        // validate game install path via adapter (walks up ancestors if needed)
        if (!string.IsNullOrWhiteSpace(cfg.GameInstallPath))
        {
            var (resolvedPath, error) = _adapter.ValidateGameInstallPath(cfg.GameInstallPath);
            if (resolvedPath is null)
            {
                Send(IpcResponse.Error("INIT_CONFIG", error!));
                return;
            }
            cfg.GameInstallPath = resolvedPath;
        }

        // validate save folder via adapter
        if (!string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            var (resolvedPath, error) = _adapter.ValidateSaveFolderPath(cfg.SaveFolderPath);
            if (resolvedPath is null)
            {
                Send(IpcResponse.Error("INIT_CONFIG", error!));
                return;
            }
            cfg.SaveFolderPath = resolvedPath;
        }

        // merge sensitive fields from existing config if user left them blank
        var existing = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.Token) && !string.IsNullOrWhiteSpace(existing.Token))
            cfg.Token = existing.Token;
        if (string.IsNullOrWhiteSpace(cfg.SshPassphrase) && !string.IsNullOrWhiteSpace(existing.SshPassphrase))
            cfg.SshPassphrase = existing.SshPassphrase;

        if (!cfg.IsConfigured)
        {
            Send(IpcResponse.Error("INIT_CONFIG", "请填写所有配置项"));
            return;
        }

        // invalidate cache so we re-read fresh after save
        _configService.InvalidateCache();
        _configService.SaveConfig(cfg);

        // resolve the actual target path — adapter decides if it's {install}\Mods or install itself
        var targetModPath = _adapter.ResolveModPath(cfg.GameInstallPath) ?? _configService.Workspace.GameModPath;

        // check if remote URL changed — if so, nuke the old repo and re-clone
        // (could be a completely different repo, can't just update the remote)
        var needsClone = !_gitService.IsRepoValid;
        if (!needsClone)
        {
            var currentUrl = _gitService.GetCurrentRemoteUrl();
            if (!string.Equals(currentUrl, cfg.RepoUrl, StringComparison.Ordinal))
                needsClone = true;
        }

        if (needsClone)
        {
            Send(IpcResponse.Progress("INIT_CONFIG", "正在克隆仓库，请稍候..."));

            // detach junction so deleting Repo/ doesn't wipe the user's mods
            if (_junctionService.IsJunction(targetModPath))
                _junctionService.RemoveJunction(targetModPath);

            // stash mod files before nuking Repo/ — we'll put them back after clone
            var stashPath = _configService.RepoPath + "_stash";
            FileSystemHelper.ForceDeleteDirectory(stashPath);
            if (Directory.Exists(_configService.RepoPath))
                Directory.Move(_configService.RepoPath, stashPath);

            FileSystemHelper.ForceDeleteDirectory(_configService.GitDirPath);

            _gitService.CloneRepo();

            // restore mod files into the fresh Repo/ so the user's mods survive
            if (Directory.Exists(stashPath))
            {
                _junctionService.FallbackCopy(stashPath, _configService.RepoPath);
                FileSystemHelper.ForceDeleteDirectory(stashPath);
            }
        }

        // set up junction: backup existing folder, then link to repo working tree
        _junctionHelper.EnsureJunction(targetModPath, _configService.RepoPath);

        Send(IpcResponse.Success("INIT_CONFIG", new { message = "配置完成，仓库已就绪！" }));
    }
}
