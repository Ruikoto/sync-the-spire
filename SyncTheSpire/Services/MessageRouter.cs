using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;

namespace SyncTheSpire.Services;

public class MessageRouter
{
    private readonly CoreWebView2 _webView;
    private readonly ConfigService _configService;
    private readonly GitService _gitService;
    private readonly JunctionService _junctionService;
    private readonly SynchronizationContext _uiContext;

    public MessageRouter(
        CoreWebView2 webView,
        ConfigService configService,
        GitService gitService,
        JunctionService junctionService)
    {
        _webView = webView;
        _configService = configService;
        _gitService = gitService;
        _junctionService = junctionService;
        // capture the UI SynchronizationContext so background threads can post back
        _uiContext = SynchronizationContext.Current
                     ?? throw new InvalidOperationException("MessageRouter must be created on the UI thread");
    }

    public void HandleMessage(string rawJson)
    {
        IpcRequest? req;
        try
        {
            // WebView2 sends WebMessageAsJson which is already a JSON string,
            // but it wraps plain strings in quotes. Our frontend sends JSON objects
            // so we may need to unwrap one layer if it got double-serialized.
            var trimmed = rawJson.Trim();
            if (trimmed.StartsWith("\""))
            {
                // double-encoded string from WebView2, unwrap first
                var inner = JsonSerializer.Deserialize<string>(trimmed);
                req = JsonSerializer.Deserialize<IpcRequest>(inner!);
            }
            else
            {
                req = JsonSerializer.Deserialize<IpcRequest>(trimmed);
            }
        }
        catch
        {
            Send(IpcResponse.Error("UNKNOWN", "Invalid JSON"));
            return;
        }

        if (req is null)
        {
            Send(IpcResponse.Error("UNKNOWN", "Empty request"));
            return;
        }

        // run everything off the UI thread so we don't freeze the window
        Task.Run(() => Route(req));
    }

    private void Route(IpcRequest req)
    {
        try
        {
            switch (req.Action)
            {
                case "GET_STATUS":
                    HandleGetStatus();
                    break;

                case "INIT_CONFIG":
                    HandleInitConfig(req.Payload);
                    break;

                case "GET_BRANCHES":
                    HandleGetBranches();
                    break;

                case "SWITCH_TO_VANILLA":
                    HandleSwitchToVanilla();
                    break;

                case "SYNC_OTHER_BRANCH":
                    HandleSyncOtherBranch(req.Payload);
                    break;

                case "CREATE_MY_BRANCH":
                    HandleCreateMyBranch(req.Payload);
                    break;

                case "SAVE_AND_PUSH_MY_BRANCH":
                    HandleSaveAndPush();
                    break;

                case "RESTORE_JUNCTION":
                    HandleRestoreJunction();
                    break;

                default:
                    Send(IpcResponse.Error(req.Action, $"Unknown action: {req.Action}"));
                    break;
            }
        }
        catch (IOException ex)
        {
            // most likely file-in-use by game process
            Send(IpcResponse.Error(req.Action, $"文件被占用，请先关闭游戏再操作！\n{ex.Message}"));
        }
        catch (LibGit2Sharp.LibGit2SharpException ex) when (
            ex.Message.Contains("401") ||
            ex.Message.Contains("403") ||
            ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            Send(IpcResponse.Error(req.Action, $"鉴权失败，请检查用户名和 Token 是否正确。\n{ex.Message}"));
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            Send(IpcResponse.Error(req.Action, $"Git 操作失败：{ex.Message}"));
        }
        catch (Exception ex)
        {
            Send(IpcResponse.Error(req.Action, $"操作失败：{ex.Message}"));
        }
    }

    // ── action handlers ──────────────────────────────────────────────────

    private void HandleGetStatus()
    {
        var cfg = _configService.LoadConfig();
        var repoExists = Directory.Exists(Path.Combine(_configService.RepoPath, ".git"));

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
            data = new
            {
                isConfigured = true,
                currentBranch = _gitService.GetCurrentBranch(),
                isJunctionActive = isJunction,
                hasLocalChanges = _gitService.HasLocalChanges()
            };
        }

        Send(IpcResponse.Success("GET_STATUS", data));
    }

    private void HandleInitConfig(JsonElement? payload)
    {
        if (payload is null)
        {
            Send(IpcResponse.Error("INIT_CONFIG", "Missing payload"));
            return;
        }

        var raw = payload.Value.GetRawText();
        var cfg = JsonSerializer.Deserialize<AppConfig>(raw);
        if (cfg is null || !cfg.IsConfigured)
        {
            Send(IpcResponse.Error("INIT_CONFIG", "请填写所有配置项"));
            return;
        }

        _configService.SaveConfig(cfg);

        Send(IpcResponse.Progress("INIT_CONFIG", "正在克隆仓库，请稍候..."));

        // clone if repo doesn't exist yet
        if (!Directory.Exists(Path.Combine(_configService.RepoPath, ".git")))
        {
            if (Directory.Exists(_configService.RepoPath))
                Directory.Delete(_configService.RepoPath, true);

            _gitService.CloneRepo();
        }

        // set up junction: backup existing game mod folder, then create junction
        EnsureJunction(cfg.GameModPath);

        Send(IpcResponse.Success("INIT_CONFIG", new { message = "配置完成，仓库已就绪！" }));
    }

    private void HandleGetBranches()
    {
        Send(IpcResponse.Progress("GET_BRANCHES", "正在获取分支列表..."));

        var branches = _gitService.GetRemoteBranches();
        var current = _gitService.GetCurrentBranch();

        Send(IpcResponse.Success("GET_BRANCHES", new { branches, currentBranch = current }));
    }

    private void HandleSwitchToVanilla()
    {
        var cfg = _configService.LoadConfig();

        // silently save any local changes first
        _gitService.SilentCommitIfDirty();

        // just remove the junction, real files stay safe in AppData
        _junctionService.RemoveJunction(cfg.GameModPath);

        Send(IpcResponse.Success("SWITCH_TO_VANILLA", new { message = "已切换到纯净模式，Mod 文件夹已断开。" }));
    }

    private void HandleSyncOtherBranch(JsonElement? payload)
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
        EnsureJunction(cfg.GameModPath);

        Send(IpcResponse.Success("SYNC_OTHER_BRANCH", new { message = $"已同步到 {branchName}" }));
    }

    private void HandleCreateMyBranch(JsonElement? payload)
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
        EnsureJunction(cfg.GameModPath);

        Send(IpcResponse.Success("CREATE_MY_BRANCH", new { message = $"分支 {branchName} 已创建" }));
    }

    private void HandleSaveAndPush()
    {
        Send(IpcResponse.Progress("SAVE_AND_PUSH_MY_BRANCH", "正在保存并上传..."));

        _gitService.CommitAndPush();

        Send(IpcResponse.Success("SAVE_AND_PUSH_MY_BRANCH", new { message = "已保存并上传！" }));
    }

    private void HandleRestoreJunction()
    {
        var cfg = _configService.LoadConfig();
        EnsureJunction(cfg.GameModPath);

        Send(IpcResponse.Success("RESTORE_JUNCTION", new { message = "Mod 文件夹已恢复连接。" }));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private void EnsureJunction(string gameModPath)
    {
        if (_junctionService.IsJunction(gameModPath))
            return; // already good

        // backup existing real folder if it's not a junction
        if (Directory.Exists(gameModPath))
        {
            var backupPath = gameModPath.TrimEnd('\\', '/') + "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            Directory.Move(gameModPath, backupPath);
        }

        var ok = _junctionService.CreateJunction(gameModPath, _configService.RepoPath);
        if (!ok)
        {
            // fallback: copy files instead
            Send(IpcResponse.Progress("JUNCTION_FALLBACK", "Junction 创建失败，降级为复制模式..."));
            _junctionService.FallbackCopy(_configService.RepoPath, gameModPath);
        }
    }

    private void Send(IpcResponse response)
    {
        var json = response.ToJson();
        // PostWebMessageAsString must be called on the UI thread
        _uiContext.Post(_ => _webView.PostWebMessageAsString(json), null);
    }
}
