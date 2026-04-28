using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Adapters;
using SyncTheSpire.Helpers;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class GitBranchHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly GitService _gitService;
    private readonly NsfwDetectionService _nsfwDetection;
    private readonly ModScannerService _modScanner;
    private readonly JunctionService _junctionService;
    private readonly JunctionHelper _junctionHelper;
    private readonly IGameAdapter _adapter;
    private readonly WorkspaceManager _workspaceManager;

    public GitBranchHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        GitService gitService,
        NsfwDetectionService nsfwDetection,
        ModScannerService modScanner,
        JunctionService junctionService,
        JunctionHelper junctionHelper,
        IGameAdapter adapter,
        WorkspaceManager workspaceManager)
        : base(webView, uiContext)
    {
        _configService = configService;
        _gitService = gitService;
        _nsfwDetection = nsfwDetection;
        _modScanner = modScanner;
        _junctionService = junctionService;
        _junctionHelper = junctionHelper;
        _adapter = adapter;
        _workspaceManager = workspaceManager;
    }

    public void HandleGetBranches()
    {
        // guard: bail out if workspace isn't configured or repo isn't ready
        // this can happen when a stale front-end request lands after switching to an unconfigured workspace
        if (!_configService.Workspace.IsConfigured || !_gitService.IsRepoValid)
        {
            Send(IpcResponse.Success("GET_BRANCHES", new { branches = Array.Empty<object>(), currentBranch = (string?)null }));
            return;
        }

        var branches = _gitService.GetRemoteBranches();
        var current = _gitService.GetCurrentBranch();

        // scan all branches for NSFW signals (folder names, mod names, etc.)
        var nsfwMap = _nsfwDetection.CheckBranchesNsfw(branches.Select(b => b.Name));

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
        if (payload is null || !payload.Value.TryGetProperty("branchName", out var bnEl))
        {
            Send(IpcResponse.Error("GET_BRANCH_MODS", "Missing branch name"));
            return;
        }
        var branchName = bnEl.GetString();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            Send(IpcResponse.Error("GET_BRANCH_MODS", "Missing branch name"));
            return;
        }

        // scan can take seconds on large repos (recursive tree walk + JSON deserialization).
        // run off the UI thread; Send marshals back via the captured uiContext.
        // stale results from a previously-selected branch are filtered by the frontend
        // using the branchName field in the response.
        Task.Run(() =>
        {
            try
            {
                var mods = _modScanner.GetBranchMods(branchName);
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
        });
    }

    public void HandleGetModDiff()
    {
        try
        {
            var branch = _gitService.GetCurrentBranch();
            var local = _modScanner.GetLocalMods()
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new { id = m.Id, name = m.Name, author = m.Author, version = m.Version });
            var remote = string.IsNullOrEmpty(branch)
                ? Enumerable.Empty<object>()
                : _modScanner.GetBranchMods(branch)
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new { id = m.Id, name = m.Name, author = m.Author, version = m.Version });

            Send(IpcResponse.Success("GET_MOD_DIFF", new { local, remote }));
        }
        catch (Exception ex)
        {
            LogService.Error("[GET_MOD_DIFF] Failed", ex);
            Send(IpcResponse.Error("GET_MOD_DIFF", $"获取 Mod 差异失败：{ex.Message}"));
        }
    }

    public void HandleSwitchToVanilla()
    {
        // silently save any local changes first
        _gitService.SilentCommitIfDirty();

        if (_adapter.SupportsJunction)
        {
            // just remove the junction, real files stay safe in AppData
            _junctionService.RemoveJunction(_configService.Workspace.GameModPath);
        }

        Send(IpcResponse.Success("SWITCH_TO_VANILLA", new { message = "已切换到纯净模式，Mod 文件夹已断开。" }));
    }

    public void HandleSyncOtherBranch(JsonElement? payload)
    {
        if (payload is null || !payload.Value.TryGetProperty("branchName", out var bnEl))
        {
            Send(IpcResponse.Error("SYNC_OTHER_BRANCH", "请选择一个分支"));
            return;
        }
        var branchName = bnEl.GetString();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            Send(IpcResponse.Error("SYNC_OTHER_BRANCH", "请选择一个分支"));
            return;
        }

        Send(IpcResponse.Progress("SYNC_OTHER_BRANCH", $"正在同步 {branchName}..."));

        // wire up fetch progress + LFS warnings (LFS checkout may fail or git-lfs may be missing —
        // user must see it since otherwise the working tree silently contains pointer text files)
        _gitService.OnTransferProgress = p =>
            Send(IpcResponse.Progress("SYNC_OTHER_BRANCH", $"正在同步 {branchName}... {p.Percent}%", p.Percent, p.Detail));
        _gitService.OnLfsMessage = msg =>
            Send(IpcResponse.Progress("SYNC_OTHER_BRANCH", msg));

        // save current work first
        _gitService.SilentCommitIfDirty();

        try { _gitService.ForceCheckoutBranch(branchName); }
        finally
        {
            _gitService.OnTransferProgress = null;
            _gitService.OnLfsMessage = null;
        }

        // make sure junction is pointing correctly
        if (_adapter.SupportsJunction)
            _junctionHelper.EnsureJunction(_configService.Workspace.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("SYNC_OTHER_BRANCH", new { message = $"已同步到 {branchName}" }));
    }

    public void HandleCreateMyBranch(JsonElement? payload)
    {
        if (payload is null || !payload.Value.TryGetProperty("branchName", out var bnEl))
        {
            Send(IpcResponse.Error("CREATE_MY_BRANCH", "请输入分支名称"));
            return;
        }
        var branchName = bnEl.GetString();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            Send(IpcResponse.Error("CREATE_MY_BRANCH", "请输入分支名称"));
            return;
        }

        Send(IpcResponse.Progress("CREATE_MY_BRANCH", $"正在创建分支 {branchName}..."));

        _gitService.SilentCommitIfDirty();
        _gitService.CreateBranch(branchName);

        if (_adapter.SupportsJunction)
            _junctionHelper.EnsureJunction(_configService.Workspace.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("CREATE_MY_BRANCH", new { message = $"分支 {branchName} 已创建" }));
    }

    public void HandleSaveAndPush()
    {
        if (_gitService.IsOnInitBranch)
        {
            Send(IpcResponse.Error("SAVE_AND_PUSH_MY_BRANCH", "请先选择或创建一个分支"));
            return;
        }

        var ws = _configService.Workspace;

        // preflight: scan for oversized files before committing
        if (ws.MaxFileSizeMode != "unlimited")
        {
            var limit = _gitService.GetEffectiveSizeLimitBytes();
            var largeFiles = _gitService.ScanLargeFiles(limit);
            if (largeFiles.Count > 0)
            {
                var limitMib = limit == long.MaxValue ? 0 : (int)(limit / (1024 * 1024));
                var autoReason = ws.MaxFileSizeMode == "auto"
                    ? GetAutoLimitReason(ws.RepoUrl, limitMib)
                    : null;

                Send(IpcResponse.Conflict("SAVE_AND_PUSH_MY_BRANCH", new
                {
                    kind = "largeFiles",
                    files = largeFiles.Select(f => new { path = f.RelativePath, sizeMib = (double)f.SizeBytes / (1024 * 1024) }).ToArray(),
                    limitMib,
                    autoReason
                }));
                return;
            }
        }

        Send(IpcResponse.Progress("SAVE_AND_PUSH_MY_BRANCH", "正在保存并上传..."));

        _gitService.OnTransferProgress = p =>
            Send(IpcResponse.Progress("SAVE_AND_PUSH_MY_BRANCH", $"正在上传... {p.Percent}%", p.Percent, p.Detail));

        var pushed = _gitService.CommitAndPush();
        _gitService.OnTransferProgress = null;

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

    private static string GetAutoLimitReason(string repoUrl, int limitMib)
    {
        var host = GitService.GetRepoHost(repoUrl);
        return $"自动检测到当前平台 ({host}) 的文件大小限制为 {limitMib} MiB";
    }

    private bool IsGiteeRepo() =>
        GitService.GetRepoHost(_configService.Workspace.RepoUrl).Contains("gitee.com");

    public void HandleForcePush()
    {
        // L1 fix: guard against force pushing on init branch
        if (_gitService.IsOnInitBranch)
        {
            Send(IpcResponse.Error("FORCE_PUSH", "请先选择或创建一个分支"));
            return;
        }

        Send(IpcResponse.Progress("FORCE_PUSH", "正在覆盖云端..."));
        _gitService.OnTransferProgress = p =>
            Send(IpcResponse.Progress("FORCE_PUSH", $"正在覆盖云端... {p.Percent}%", p.Percent, p.Detail));
        _gitService.ForcePush();
        _gitService.OnTransferProgress = null;
        Send(IpcResponse.Success("FORCE_PUSH", new { message = "已覆盖云端配置！" }));
    }

    public void HandleResetToRemote()
    {
        // L1 fix: guard against reset on init branch
        if (_gitService.IsOnInitBranch)
        {
            Send(IpcResponse.Error("RESET_TO_REMOTE", "请先选择或创建一个分支"));
            return;
        }

        Send(IpcResponse.Progress("RESET_TO_REMOTE", "正在同步云端配置..."));
        _gitService.OnTransferProgress = p =>
            Send(IpcResponse.Progress("RESET_TO_REMOTE", $"正在同步云端配置... {p.Percent}%", p.Percent, p.Detail));
        _gitService.OnLfsMessage = msg =>
            Send(IpcResponse.Progress("RESET_TO_REMOTE", msg));
        try { _gitService.ResetToRemote(); }
        finally
        {
            _gitService.OnTransferProgress = null;
            _gitService.OnLfsMessage = null;
        }

        if (_adapter.SupportsJunction)
            _junctionHelper.EnsureJunction(_configService.Workspace.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("RESET_TO_REMOTE", new { message = "已同步为云端配置！" }));
    }

    public void HandlePreflightExcludeLargeFiles(JsonElement? payload)
    {
        if (payload is null)
        {
            Send(IpcResponse.Error("PREFLIGHT_EXCLUDE_LARGE_FILES", "Missing payload"));
            return;
        }

        var paths = payload.Value.GetProperty("files")
            .EnumerateArray()
            .Select(f => f.GetString() ?? "")
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        // append each path as an exclude rule; also save to workspace config for reference
        _gitService.AppendExcludeRules(paths);

        var ws = _configService.Workspace;
        foreach (var p in paths)
            if (!ws.ExcludedLargeFiles.Contains(p))
                ws.ExcludedLargeFiles.Add(p);
        _configService.SaveWorkspace();

        // now proceed with the actual commit+push
        Send(IpcResponse.Progress("PREFLIGHT_EXCLUDE_LARGE_FILES", "正在保存并上传..."));

        _gitService.OnTransferProgress = p2 =>
            Send(IpcResponse.Progress("PREFLIGHT_EXCLUDE_LARGE_FILES", $"正在上传... {p2.Percent}%", p2.Percent, p2.Detail));

        var pushed = _gitService.CommitAndPush();
        _gitService.OnTransferProgress = null;

        if (!pushed)
        {
            Send(IpcResponse.Conflict("PREFLIGHT_EXCLUDE_LARGE_FILES", new
            {
                message = "云端存在更新的配置，与本地改动冲突。"
            }));
            return;
        }

        Send(IpcResponse.Success("PREFLIGHT_EXCLUDE_LARGE_FILES", new { message = "已保存并上传！" }));
    }

    public void HandlePreflightEnableLfs(JsonElement? payload)
    {
        // track large files by their exact paths — no extension guessing
        var filePaths = payload?.GetProperty("files").EnumerateArray()
            .Select(f => f.GetString() ?? "").Where(f => f != "")
            .Select(f => f.Replace('\\', '/'))
            .ToList() ?? [];

        if (filePaths.Count == 0)
        {
            Send(IpcResponse.Error("PREFLIGHT_ENABLE_LFS", "未收到超限文件列表，无法启用 LFS。"));
            return;
        }

        // Gitee free tier doesn't support LFS — bounce the call back as a Conflict so the
        // frontend can show a real confirm dialog. A Progress-tagged warning flashes past
        // before the user can read it. `giteeAck: true` in payload means the user has already
        // confirmed they want to proceed anyway.
        var giteeAck = payload?.TryGetProperty("giteeAck", out var ackEl) == true &&
                       ackEl.ValueKind == JsonValueKind.True;
        if (!giteeAck && IsGiteeRepo())
        {
            Send(IpcResponse.Conflict("PREFLIGHT_ENABLE_LFS", new
            {
                kind = "gitee",
                files = filePaths,
                message = "Gitee 免费账户不支持 Git LFS，启用后推送会失败。是否仍要继续？"
            }));
            return;
        }

        Send(IpcResponse.Progress("PREFLIGHT_ENABLE_LFS", "正在安装 Git LFS..."));
        _gitService.OnLfsMessage = msg =>
            Send(IpcResponse.Progress("PREFLIGHT_ENABLE_LFS", msg));
        try { _gitService.EnableLfs(); }
        finally { _gitService.OnLfsMessage = null; }

        Send(IpcResponse.Progress("PREFLIGHT_ENABLE_LFS", $"正在标记 {filePaths.Count} 个大文件为 LFS 存储..."));
        _gitService.TrackLfsPatterns(filePaths);

        Send(IpcResponse.Progress("PREFLIGHT_ENABLE_LFS", "正在保存并上传..."));
        _gitService.OnTransferProgress = p =>
            Send(IpcResponse.Progress("PREFLIGHT_ENABLE_LFS",
                $"正在上传... {p.Percent}%", p.Percent, p.Detail));
        bool pushed;
        try { pushed = _gitService.CommitAndPush(); }
        finally { _gitService.OnTransferProgress = null; }

        if (!pushed)
        {
            Send(IpcResponse.Conflict("PREFLIGHT_ENABLE_LFS",
                new { message = "云端存在更新的配置，与本地改动冲突。" }));
            return;
        }

        Send(IpcResponse.Success("PREFLIGHT_ENABLE_LFS", new
        {
            message = $"已启用 Git LFS，{filePaths.Count} 个大文件将以 LFS 方式上传。",
            trackedPaths = filePaths
        }));
    }

    public void HandleMigrateExistingToLfs(JsonElement? payload)
    {
        // auto-scan — no user input required
        Send(IpcResponse.Progress("MIGRATE_EXISTING_TO_LFS", "正在扫描超限文件..."));
        var limit = _gitService.GetEffectiveSizeLimitBytes();
        var largeFiles = _gitService.ScanLargeFiles(limit);

        if (largeFiles.Count == 0)
        {
            Send(IpcResponse.Success("MIGRATE_EXISTING_TO_LFS", new
            {
                message = "当前工作区未检测到超限文件，无需迁移。"
            }));
            return;
        }

        var filePaths = largeFiles
            .Select(f => f.RelativePath.Replace('\\', '/'))
            .ToList();

        // Gitee ack — see HandlePreflightEnableLfs for rationale
        var giteeAck = payload?.TryGetProperty("giteeAck", out var ackEl) == true &&
                       ackEl.ValueKind == JsonValueKind.True;
        if (!giteeAck && IsGiteeRepo())
        {
            Send(IpcResponse.Conflict("MIGRATE_EXISTING_TO_LFS", new
            {
                kind = "gitee",
                message = "Gitee 免费账户不支持 Git LFS，启用后推送会失败。是否仍要继续？"
            }));
            return;
        }

        // gather all local non-protected branches so the history rewrite covers the whole repo
        var branches = _gitService.GetMigratableLocalBranches();
        if (branches.Count == 0)
        {
            Send(IpcResponse.Error("MIGRATE_EXISTING_TO_LFS",
                "没有可迁移的分支（受保护分支和 init 分支会被跳过）。"));
            return;
        }

        // `git lfs migrate import` refuses to run on a dirty working tree — snapshot
        // any pending edits into a commit first so the rewrite can proceed
        _gitService.SilentCommitIfDirty();

        Send(IpcResponse.Progress("MIGRATE_EXISTING_TO_LFS", "正在安装 Git LFS..."));
        _gitService.OnLfsMessage = msg =>
            Send(IpcResponse.Progress("MIGRATE_EXISTING_TO_LFS", msg));
        try { _gitService.EnableLfs(); }
        finally { _gitService.OnLfsMessage = null; }

        // skip TrackLfsPatterns: `lfs migrate import` writes its own .gitattributes entries
        // into the rewritten history. running `git lfs track` here would only dirty the
        // working tree and make migrate import refuse to run.
        Send(IpcResponse.Progress("MIGRATE_EXISTING_TO_LFS",
            $"正在重写 {branches.Count} 个分支的历史并推送（可能需要几分钟）..."));
        _gitService.MigrateToLfsAndPush(filePaths, branches);

        // record patterns for the settings UI (migrate already wrote .gitattributes in-tree)
        var ws = _configService.Workspace;
        foreach (var p in filePaths)
            if (!ws.LfsTrackedPatterns.Contains(p, StringComparer.OrdinalIgnoreCase))
                ws.LfsTrackedPatterns.Add(p);
        _configService.SaveWorkspace();

        Send(IpcResponse.Success("MIGRATE_EXISTING_TO_LFS", new
        {
            message = $"LFS 迁移完成！已将 {filePaths.Count} 个文件在 {branches.Count} 个分支上转为 LFS 存储。",
            trackedPaths = filePaths,
            migratedBranches = branches
        }));
    }

    public void HandleRebuildBranchesOrphan(JsonElement? payload)
    {
        if (payload is null)
        {
            Send(IpcResponse.Error("REBUILD_BRANCHES_ORPHAN", "Missing payload"));
            return;
        }

        var branches = payload.Value.GetProperty("branches")
            .EnumerateArray()
            .Select(b => b.GetString() ?? "")
            .Where(b => !string.IsNullOrEmpty(b))
            .ToList();

        if (branches.Count == 0)
        {
            Send(IpcResponse.Error("REBUILD_BRANCHES_ORPHAN", "没有指定分支"));
            return;
        }

        Send(IpcResponse.Progress("REBUILD_BRANCHES_ORPHAN", "正在重建分支历史..."));

        var results = _gitService.RebuildBranchesAsOrphan(branches, (branch, index, total) =>
        {
            Send(IpcResponse.Progress("REBUILD_BRANCHES_ORPHAN",
                $"正在重建 ({index + 1}/{total}): {branch}", (int)((double)(index + 1) / total * 80)));
        });

        Send(IpcResponse.Progress("REBUILD_BRANCHES_ORPHAN", "正在清理本地历史数据...", 90));

        var successCount = results.Count(r => r.Success);
        var failCount = results.Count(r => !r.Success);

        Send(IpcResponse.Success("REBUILD_BRANCHES_ORPHAN", new
        {
            results = results.Select(r => new { branch = r.Branch, success = r.Success, error = r.Error }).ToArray(),
            successCount,
            failCount
        }));
    }
}
