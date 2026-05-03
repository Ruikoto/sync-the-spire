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

        // share a single Repository across branch listing + NSFW scan to avoid
        // double-opening (LibGit2Sharp init takes file lock + loads index)
        using var repo = _gitService.OpenRepository();
        var branches = _gitService.GetRemoteBranches(repo);
        var current = _gitService.GetCurrentBranch();

        // scan all branches for NSFW signals (folder names, mod names, etc.)
        var nsfwMap = _nsfwDetection.CheckBranchesNsfw(branches.Select(b => b.Name), repo);

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

        Send(IpcResponse.Progress("SYNC_OTHER_BRANCH", "正在保存本地改动..."));

        // wire up fetch progress + stage messages + LFS warnings.
        // stage messages keep the modal informative during non-network phases (checkout/clean/lfs)
        // that emit no transfer progress; otherwise the user stares at a static modal for seconds.
        _gitService.OnTransferProgress = p =>
            Send(IpcResponse.Progress("SYNC_OTHER_BRANCH", "正在下载远端数据...", p.Percent, p.Detail));
        _gitService.OnStage = stage =>
            Send(IpcResponse.Progress("SYNC_OTHER_BRANCH", PullStageMessage(stage, branchName)));
        _gitService.OnLfsMessage = msg =>
            Send(IpcResponse.Progress("SYNC_OTHER_BRANCH", msg));

        // save current work first
        _gitService.SilentCommitIfDirty();

        try { _gitService.ForceCheckoutBranch(branchName); }
        finally
        {
            _gitService.OnTransferProgress = null;
            _gitService.OnStage = null;
            _gitService.OnLfsMessage = null;
        }

        // make sure junction is pointing correctly
        if (_adapter.SupportsJunction)
            _junctionHelper.EnsureJunction(_configService.Workspace.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("SYNC_OTHER_BRANCH", new
        {
            message = $"已同步到 {branchName}",
            lfsWarning = _gitService.LastLfsWarning
        }));
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

        // pre-push preflight — always runs. unlimited mode uses host default as advisory.
        var limit = ws.MaxFileSizeMode == "unlimited"
            ? GetHostDefaultLimitBytes(ws.RepoUrl)
            : _gitService.GetEffectiveSizeLimitBytes();

        var workTreeLarge = _gitService.ScanLargeFiles(limit);
        var unpushedLarge = _gitService.ScanLargeFilesInUnpushedCommits(limit);

        LogService.Info(
            $"[Preflight] mode={ws.MaxFileSizeMode} limit={limit / (1024 * 1024)}MiB " +
            $"workTree={workTreeLarge.Count} unpushed={unpushedLarge.Count}");

        if (workTreeLarge.Count > 0 || unpushedLarge.Count > 0)
        {
            Send(IpcResponse.Conflict("SAVE_AND_PUSH_MY_BRANCH",
                BuildPreflightConflictPayload(workTreeLarge, unpushedLarge, limit, ws)));
            return;
        }

        try
        {
            DoCommitAndPush("SAVE_AND_PUSH_MY_BRANCH");
        }
        catch (Exception ex) when (IsServerSizeRejection(ex.Message))
        {
            // server rejected for size — our local limit was too lenient (host's actual cap
            // is lower than what's hardcoded, e.g. gitcode at 99 MiB but the user's account
            // tier rejects at 50). CommitAndPush already soft-reset the just-created commit
            // so the file is back in the index as staged; re-scan with a strict 49 MiB cap
            // (the smallest known host limit) to pinpoint the culprit, then surface the
            // preflight modal with cancel / try-anyway / delete options.
            const long strictLimit = 49L * 1024 * 1024;
            var rescanWorkTree = _gitService.ScanLargeFiles(strictLimit);
            var rescanUnpushed = _gitService.ScanLargeFilesInUnpushedCommits(strictLimit);

            LogService.Warn($"[Preflight] post-rejection rescan at {strictLimit / 1024 / 1024} MiB: " +
                            $"workTree={rescanWorkTree.Count} unpushed={rescanUnpushed.Count}");

            if (rescanWorkTree.Count > 0 || rescanUnpushed.Count > 0)
            {
                Send(IpcResponse.Conflict("SAVE_AND_PUSH_MY_BRANCH",
                    BuildPreflightConflictPayload(rescanWorkTree, rescanUnpushed, strictLimit, ws)));
                return;
            }

            // rescan came up empty — let the original error bubble up to the toast path;
            // user will at least see what the server said
            throw;
        }
    }

    // shared with FriendlyGitError for cross-call detection. covers single-file size
    // rejections from common hosts (github / gitcode / gitee / gitlab / bitbucket).
    private static bool IsServerSizeRejection(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return false;
        return msg.Contains("File size exceeds", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("exceeds the maximum", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("larger than", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("HTTP 413", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("repository size limit", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("over quota", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("data quota", StringComparison.OrdinalIgnoreCase)
            || (msg.Contains("pre-receive hook declined", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("size", StringComparison.OrdinalIgnoreCase))
            // gitcode-style: server returns "exceeded limited size" in stderr
            || msg.Contains("exceeded limited size", StringComparison.OrdinalIgnoreCase)
            || (msg.Contains("超过") && msg.Contains("大小"));
    }

    /// <summary>
    /// bypass preflight and push as-is. user has acknowledged the upload may fail.
    /// </summary>
    public void HandlePreflightForcePush()
    {
        if (_gitService.IsOnInitBranch)
        {
            Send(IpcResponse.Error("PREFLIGHT_FORCE_PUSH", "请先选择或创建一个分支"));
            return;
        }
        LogService.Info("[Preflight] user chose force-try upload, bypassing preflight");
        DoCommitAndPush("PREFLIGHT_FORCE_PUSH");
    }

    /// <summary>
    /// delete the listed large files (working tree + unpushed-commits paths combined),
    /// then commit + push the deletion. for unpushed-commits paths we first soft-reset
    /// to the unpushed boundary so the deletion goes into a fresh commit on top of the
    /// remote tip rather than the bad commits being retained in history.
    /// </summary>
    public void HandlePreflightDeleteLargeFiles(JsonElement? payload)
    {
        if (payload is null)
        {
            Send(IpcResponse.Error("PREFLIGHT_DELETE_LARGE_FILES", "Missing payload"));
            return;
        }

        var workTreePaths = payload.Value.TryGetProperty("files", out var fEl) && fEl.ValueKind == JsonValueKind.Array
            ? fEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(p => p.Length > 0).ToList()
            : new List<string>();

        var unpushedPaths = payload.Value.TryGetProperty("unpushedFiles", out var uEl) && uEl.ValueKind == JsonValueKind.Array
            ? uEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(p => p.Length > 0).ToList()
            : new List<string>();

        var allPaths = workTreePaths.Concat(unpushedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allPaths.Count == 0)
        {
            Send(IpcResponse.Error("PREFLIGHT_DELETE_LARGE_FILES", "未指定要删除的文件"));
            return;
        }

        Send(IpcResponse.Progress("PREFLIGHT_DELETE_LARGE_FILES", "正在准备删除..."));

        // step 1: if there were unpushed-commit large files, soft-reset to the unpushed
        // boundary so deletions go into a clean new commit. for working-tree-only case,
        // skip this — soft reset is destructive of commit granularity.
        if (unpushedPaths.Count > 0)
        {
            try
            {
                var rs = _gitService.SoftResetToUnpushedBoundary();
                LogService.Info($"[Preflight Delete] soft-reset {rs.RevertedCommitCount} unpushed commits");
            }
            catch (Exception ex)
            {
                LogService.Error("[PREFLIGHT_DELETE_LARGE_FILES] soft reset failed", ex);
                Send(IpcResponse.Error("PREFLIGHT_DELETE_LARGE_FILES",
                    $"撤销未推送提交失败：{ex.Message}"));
                return;
            }
        }

        // step 2: physically delete the files from working tree.
        int deleted = 0;
        foreach (var rel in allPaths)
        {
            var fullPath = Path.Combine(_configService.WorkTreePath,
                rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    deleted++;
                    LogService.Info($"[Preflight Delete] removed {rel}");
                }
                else
                {
                    LogService.Warn($"[Preflight Delete] file not found: {rel}");
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[Preflight Delete] failed to remove {rel}: {ex.Message}");
            }
        }

        Send(IpcResponse.Progress("PREFLIGHT_DELETE_LARGE_FILES",
            $"已删除 {deleted} 个文件，正在提交..."));

        // step 3: commit + push (StageAll picks up deletions naturally).
        DoCommitAndPush("PREFLIGHT_DELETE_LARGE_FILES");
    }

    // shared helper used by SaveAndPush, PreflightForcePush, PreflightDeleteLargeFiles
    private void DoCommitAndPush(string action)
    {
        Send(IpcResponse.Progress(action, "正在保存并上传..."));
        _gitService.OnTransferProgress = p =>
            Send(IpcResponse.Progress(action, "正在上传到云端...", p.Percent, p.Detail));

        bool pushed;
        try
        {
            pushed = _gitService.CommitAndPush();
        }
        finally
        {
            _gitService.OnTransferProgress = null;
        }

        if (!pushed)
        {
            Send(IpcResponse.Conflict(action, new
            {
                message = "云端存在更新的配置，与本地改动冲突。"
            }));
            return;
        }

        Send(IpcResponse.Success(action, new { message = "已保存并上传！" }));
    }

    private object BuildPreflightConflictPayload(
        List<GitService.LargeFile> workTree,
        List<GitService.UnpushedLargeFile> unpushed,
        long limit,
        WorkspaceConfig ws)
    {
        var limitMib = limit == long.MaxValue ? 0 : (int)(limit / (1024 * 1024));
        var autoReason = ws.MaxFileSizeMode == "auto"
            ? GetAutoLimitReason(ws.RepoUrl, limitMib) : null;
        var advisory = ws.MaxFileSizeMode == "unlimited";  // unlimited = advisory only

        string kind;
        if (workTree.Count > 0 && unpushed.Count > 0) kind = "largeFilesMixed";
        else if (workTree.Count > 0)                   kind = "largeFiles";
        else                                           kind = "largeFilesInUnpushed";

        return new
        {
            kind,
            limitMib,
            autoReason,
            advisory,    // frontend can show "advisory only" hint when unlimited
            files = workTree.Select(f => new
            {
                path = f.RelativePath,
                sizeMib = (double)f.SizeBytes / (1024 * 1024)
            }).ToArray(),
            unpushedFiles = unpushed.Select(f => new
            {
                path = f.RelativePath,
                sizeMib = (double)f.SizeBytes / (1024 * 1024),
                commitSha = f.CommitSha[..7],
                commitSubject = f.CommitSubject
            }).ToArray(),
            unpushedCommitCount = unpushed.Select(f => f.CommitSha).Distinct().Count()
        };
    }

    // host-default limit for unlimited mode (advisory scan) and fallback display.
    private long GetHostDefaultLimitBytes(string repoUrl)
    {
        // mirror GetEffectiveSizeLimitBytes' auto-mode logic without honoring user override.
        // if the host is unknown, use 49 MiB as the conservative default.
        var host = GitService.GetRepoHost(repoUrl);
        var hostLimits = new (string HostContains, int Mib)[]
        {
            ("github.com",    99),
            ("atomgit.com",   99),
            ("gitcode.com",   99),
            ("gitlab.com",    99),
            ("bitbucket.org", 99),
            ("gitee.com",     49),
        };
        foreach (var (h, mib) in hostLimits)
            if (host.Contains(h, StringComparison.OrdinalIgnoreCase))
                return (long)mib * 1024 * 1024;
        return 49L * 1024 * 1024;
    }

    private static string GetAutoLimitReason(string repoUrl, int limitMib)
    {
        var host = GitService.GetRepoHost(repoUrl);
        return $"自动检测到当前平台 ({host}) 的文件大小限制为 {limitMib} MiB";
    }

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
            Send(IpcResponse.Progress("FORCE_PUSH", "正在上传到云端...", p.Percent, p.Detail));
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

        var resetBranch = _gitService.GetCurrentBranch();
        Send(IpcResponse.Progress("RESET_TO_REMOTE", "正在准备同步云端配置..."));
        _gitService.OnTransferProgress = p =>
            Send(IpcResponse.Progress("RESET_TO_REMOTE", "正在下载远端数据...", p.Percent, p.Detail));
        _gitService.OnStage = stage =>
            Send(IpcResponse.Progress("RESET_TO_REMOTE", PullStageMessage(stage, resetBranch)));
        _gitService.OnLfsMessage = msg =>
            Send(IpcResponse.Progress("RESET_TO_REMOTE", msg));
        try { _gitService.ResetToRemote(); }
        finally
        {
            _gitService.OnTransferProgress = null;
            _gitService.OnStage = null;
            _gitService.OnLfsMessage = null;
        }

        if (_adapter.SupportsJunction)
            _junctionHelper.EnsureJunction(_configService.Workspace.GameModPath, _configService.RepoPath);

        Send(IpcResponse.Success("RESET_TO_REMOTE", new
        {
            message = "已同步为云端配置！",
            lfsWarning = _gitService.LastLfsWarning
        }));
    }

    public void HandleResetUnpushedCommits()
    {
        if (_gitService.IsOnInitBranch)
        {
            Send(IpcResponse.Error("RESET_UNPUSHED_COMMITS", "请先选择或创建一个分支"));
            return;
        }

        Send(IpcResponse.Progress("RESET_UNPUSHED_COMMITS", "正在撤销未推送的提交..."));

        var result = _gitService.SoftResetToUnpushedBoundary();

        var msg = result.RevertedCommitCount == 0
            ? "没有未推送的提交需要撤销。"
            : $"已撤销 {result.RevertedCommitCount} 个未推送的提交。这些更改已还原到待提交状态，请重新选择处理方式。";

        Send(IpcResponse.Success("RESET_UNPUSHED_COMMITS", new
        {
            message = msg,
            revertedCommitCount = result.RevertedCommitCount,
            resetTarget = result.Target
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
            results = results.Select(r => new
            {
                branch = r.Branch,
                success = r.Success,
                // localise the raw git error so the frontend can toast it directly without classification
                error = r.Success ? null : MessageRouter.FriendlyGitError(r.Error ?? ""),
            }).ToArray(),
            successCount,
            failCount
        }));
    }

    // map GitService stage keys to user-facing pull/sync messages
    private static string PullStageMessage(string stage, string branchName) => stage switch
    {
        "fetching"        => "正在获取云端最新内容...",
        "checking-out"    => $"正在切换到 {branchName}...",
        "resetting"       => "正在应用远端版本...",
        "cleaning"        => "正在清理工作区...",
        "lfs-install"     => "正在准备大文件支持...",
        "lfs-downloading" => "正在下载大文件，可能耗时较长...",
        _ => stage
    };
}
