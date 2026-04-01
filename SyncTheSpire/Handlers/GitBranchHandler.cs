using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Helpers;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class GitBranchHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly GitService _gitService;
    private readonly JunctionService _junctionService;
    private readonly JunctionHelper _junctionHelper;

    public GitBranchHandler(
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

    public void HandleGetBranches()
    {
        var branches = _gitService.GetRemoteBranches();
        var current = _gitService.GetCurrentBranch();

        // scan all branches for NSFW signals (folder names, mod names, etc.)
        var nsfwMap = _gitService.CheckBranchesNsfw(branches.Select(b => b.Name));

        // flatten BranchInfo to plain objects so JSON stays predictable
        var list = branches.Select(b =>
        {
            var nsfw = nsfwMap.GetValueOrDefault(b.Name);
            return new
            {
                name = b.Name,
                author = b.Author,
                lastModified = b.LastModified.ToUnixTimeMilliseconds(),
                isNsfw = nsfw?.IsNsfw ?? false,
                nsfwReasons = nsfw?.Reasons ?? []
            };
        });

        Send(IpcResponse.Success("GET_BRANCHES", new { branches = list, currentBranch = current }));
    }

    public void HandleGetBranchMods(JsonElement? payload)
    {
        var branchName = payload?.GetProperty("branchName").GetString();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            Send(IpcResponse.Error("GET_BRANCH_MODS", "Missing branch name"));
            return;
        }

        try
        {
            var mods = _gitService.GetBranchMods(branchName);
            var sorted = mods
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new
                {
                    id = m.Id,
                    name = m.Name,
                    author = m.Author,
                    description = m.Description,
                    version = m.Version
                });

            Send(IpcResponse.Success("GET_BRANCH_MODS", new { branchName, mods = sorted }));
        }
        catch (Exception ex)
        {
            LogService.Error($"[GET_BRANCH_MODS] Failed to scan branch {branchName}", ex);
            Send(IpcResponse.Error("GET_BRANCH_MODS", $"读取 Mod 列表失败：{ex.Message}"));
        }
    }

    public void HandleSwitchToVanilla()
    {
        var cfg = _configService.LoadConfig();

        // silently save any local changes first
        _gitService.SilentCommitIfDirty();

        // just remove the junction, real files stay safe in AppData
        _junctionService.RemoveJunction(cfg.GameModPath);

        Send(IpcResponse.Success("SWITCH_TO_VANILLA", new { message = "已切换到纯净模式，Mod 文件夹已断开。" }));
    }

    public void HandleSyncOtherBranch(JsonElement? payload)
    {
        var branchName = payload?.GetProperty("branchName").GetString();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            Send(IpcResponse.Error("SYNC_OTHER_BRANCH", "请选择一个分支"));
            return;
        }

        Send(IpcResponse.Progress("SYNC_OTHER_BRANCH", $"正在同步 {branchName}..."));

        // save current work first
        _gitService.SilentCommitIfDirty();

        _gitService.ForceCheckoutBranch(branchName);

        // make sure junction is pointing correctly
        var cfg = _configService.LoadConfig();
        _junctionHelper.EnsureJunction(cfg.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("SYNC_OTHER_BRANCH", new { message = $"已同步到 {branchName}" }));
    }

    public void HandleCreateMyBranch(JsonElement? payload)
    {
        var branchName = payload?.GetProperty("branchName").GetString();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            Send(IpcResponse.Error("CREATE_MY_BRANCH", "请输入分支名称"));
            return;
        }

        Send(IpcResponse.Progress("CREATE_MY_BRANCH", $"正在创建分支 {branchName}..."));

        _gitService.SilentCommitIfDirty();
        _gitService.CreateBranch(branchName);

        var cfg = _configService.LoadConfig();
        _junctionHelper.EnsureJunction(cfg.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("CREATE_MY_BRANCH", new { message = $"分支 {branchName} 已创建" }));
    }

    public void HandleSaveAndPush()
    {
        if (_gitService.IsOnInitBranch)
        {
            Send(IpcResponse.Error("SAVE_AND_PUSH_MY_BRANCH", "请先选择或创建一个分支"));
            return;
        }

        Send(IpcResponse.Progress("SAVE_AND_PUSH_MY_BRANCH", "正在保存并上传..."));

        var pushed = _gitService.CommitAndPush();

        if (!pushed)
        {
            // branches diverged — let the user pick a resolution
            Send(IpcResponse.Conflict("SAVE_AND_PUSH_MY_BRANCH", new
            {
                message = "云端存在更新的配置，与本地改动冲突。"
            }));
            return;
        }

        Send(IpcResponse.Success("SAVE_AND_PUSH_MY_BRANCH", new { message = "已保存并上传！" }));
    }

    public void HandleForcePush()
    {
        Send(IpcResponse.Progress("FORCE_PUSH", "正在覆盖云端..."));
        _gitService.ForcePush();
        Send(IpcResponse.Success("FORCE_PUSH", new { message = "已覆盖云端配置！" }));
    }

    public void HandleResetToRemote()
    {
        Send(IpcResponse.Progress("RESET_TO_REMOTE", "正在同步云端配置..."));
        _gitService.ResetToRemote();

        var cfg = _configService.LoadConfig();
        _junctionHelper.EnsureJunction(cfg.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("RESET_TO_REMOTE", new { message = "已同步为云端配置！" }));
    }
}
