using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
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

    public ConfigHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        GitService gitService,
        JunctionService junctionService,
        JunctionHelper junctionHelper)
        : base(webView, uiContext)
    {
        _configService = configService;
        _gitService = gitService;
        _junctionService = junctionService;
        _junctionHelper = junctionHelper;
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
        var cfg = _configService.LoadConfig();
        var repoExists = _gitService.IsRepoValid;

        object data;
        if (!cfg.IsConfigured || !repoExists)
        {
            data = new
            {
                isConfigured = false,
                currentBranch = (string?)null,
                isJunctionActive = false,
                hasLocalChanges = false
            };
        }
        else
        {
            var isJunction = _junctionService.IsJunction(cfg.GameModPath);
            var branch = _gitService.GetCurrentBranch();
            var isInit = branch == GitService.InitBranch;
            data = new
            {
                isConfigured = true,
                currentBranch = isInit ? (string?)null : branch,
                isJunctionActive = isJunction,
                hasLocalChanges = isInit ? false : _gitService.HasLocalChanges(),
                needsBranchSelection = isInit
            };
        }

        Send(IpcResponse.Success("GET_STATUS", data));
    }

    /// <summary>
    /// fetch from remote and return ahead/behind counts -- used by the refresh button
    /// </summary>
    public void HandleRefreshSync()
    {
        var cfg = _configService.LoadConfig();
        if (!cfg.IsConfigured || !_gitService.IsRepoValid || _gitService.IsOnInitBranch)
        {
            // nothing useful to fetch, just return basic status
            HandleGetStatus();
            return;
        }

        var sync = _gitService.FetchAndGetSyncStatus();
        var isJunction = _junctionService.IsJunction(cfg.GameModPath);
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
        var gitUserName = _gitService.ReadGitGlobalConfig("user.name");
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

        // validate game install path: must contain SlayTheSpire2.exe
        // if the user picked a subdirectory, walk up to find the correct root
        if (!string.IsNullOrWhiteSpace(cfg.GameInstallPath))
        {
            var resolved = FileSystemHelper.FindAncestorContaining(cfg.GameInstallPath, "SlayTheSpire2.exe", isFile: true);
            if (resolved is null)
            {
                Send(IpcResponse.Error("INIT_CONFIG",
                    $"游戏安装路径无效：未找到 SlayTheSpire2.exe\n请确认路径是否正确：{cfg.GameInstallPath}"));
                return;
            }
            cfg.GameInstallPath = resolved;
        }

        // validate save folder: must contain profile1/ dir and profile.save file
        // walk up if necessary so the user can pick e.g. saves/profile1 and we still find saves/
        if (!string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            var resolved = FileSystemHelper.FindAncestorContaining(cfg.SaveFolderPath,
                dir => Directory.Exists(Path.Combine(dir, "profile1"))
                    && File.Exists(Path.Combine(dir, "profile.save")));
            if (resolved is null)
            {
                Send(IpcResponse.Error("INIT_CONFIG",
                    $"存档路径无效：未找到 profile1 文件夹或 profile.save 文件\n请确认路径是否正确：{cfg.SaveFolderPath}"));
                return;
            }
            cfg.SaveFolderPath = resolved;
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
            if (_junctionService.IsJunction(cfg.GameModPath))
                _junctionService.RemoveJunction(cfg.GameModPath);

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

        // set up junction: backup existing game mod folder, then create junction
        _junctionHelper.EnsureJunction(cfg.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("INIT_CONFIG", new { message = "配置完成，仓库已就绪！" }));
    }
}
