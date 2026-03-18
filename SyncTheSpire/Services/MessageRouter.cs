using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private readonly SaveBackupService _backupService;
    private readonly SaveMergeService _mergeService;
    private readonly MainForm _form;
    private readonly SynchronizationContext _uiContext;
    // only one IPC operation at a time to prevent concurrent access to git/config/filesystem
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MessageRouter(
        CoreWebView2 webView,
        ConfigService configService,
        GitService gitService,
        JunctionService junctionService,
        SaveBackupService backupService,
        SaveMergeService mergeService,
        MainForm form)
    {
        _webView = webView;
        _configService = configService;
        _gitService = gitService;
        _junctionService = junctionService;
        _backupService = backupService;
        _mergeService = mergeService;
        _form = form;
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
        // window chrome controls don't need serialization — fire and forget on UI thread
        switch (req.Action)
        {
            case "WINDOW_DRAG":
                _uiContext.Post(_ => _form.BeginDrag(), null);
                return;
            case "WINDOW_MINIMIZE":
                _uiContext.Post(_ => _form.WindowState = FormWindowState.Minimized, null);
                return;
            case "WINDOW_MAXIMIZE":
                _uiContext.Post(_ =>
                    _form.WindowState = _form.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized, null);
                return;
            case "WINDOW_CLOSE":
                _uiContext.Post(_ => _form.Close(), null);
                return;
            // PICK_FOLDER needs UI dialog — handle outside the gate to avoid blocking other IPC
            case "PICK_FOLDER":
                HandlePickFolder();
                return;
        }

        _gate.Wait();
        try
        {
            switch (req.Action)
            {
                case "GET_STATUS":
                    HandleGetStatus();
                    break;

                case "GET_VERSION":
                    HandleGetVersion();
                    break;

                case "GET_CONFIG":
                    HandleGetConfig();
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

                case "OPEN_FOLDER":
                    HandleOpenFolder(req.Payload);
                    break;

                // ── save management ──────────────────────────────────
                case "GET_SAVE_STATUS":
                    HandleGetSaveStatus();
                    break;

                case "ANALYZE_SAVE_MERGE":
                    HandleAnalyzeSaveMerge();
                    break;

                case "EXECUTE_SAVE_MERGE":
                    HandleExecuteSaveMerge(req.Payload);
                    break;

                case "UNLINK_SAVES":
                    HandleUnlinkSaves();
                    break;

                case "BACKUP_SAVES":
                    HandleBackupSaves();
                    break;

                case "GET_BACKUP_LIST":
                    HandleGetBackupList();
                    break;

                case "RESTORE_BACKUP":
                    HandleRestoreBackup(req.Payload);
                    break;

                case "DELETE_BACKUP":
                    HandleDeleteBackup(req.Payload);
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
        finally
        {
            _gate.Release();
        }
    }

    // ── action handlers ──────────────────────────────────────────────────

    private void HandleGetVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(); // x64, arm64, etc.
        Send(IpcResponse.Success("GET_VERSION", new { version, arch }));
    }

    private void HandleGetStatus()
    {
        var cfg = _configService.LoadConfig();
        var repoExists = _configService.IsRepoInitialized;

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
    /// return saved config to frontend for pre-filling the settings form
    /// strips sensitive fields (token, ssh passphrase)
    /// </summary>
    private void HandleGetConfig()
    {
        var cfg = _configService.LoadConfig();
        Send(IpcResponse.Success("GET_CONFIG", new
        {
            repoUrl = cfg.RepoUrl,
            authType = cfg.AuthType,
            username = cfg.Username,
            sshKeyPath = cfg.SshKeyPath,
            gameInstallPath = cfg.GameInstallPath,
            saveFolderPath = cfg.SaveFolderPath,
            // don't return token or sshPassphrase
        }));
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
        if (cfg is null)
        {
            Send(IpcResponse.Error("INIT_CONFIG", "请填写所有配置项"));
            return;
        }

        // validate game install path: must contain SlayTheSpire2.exe
        if (!string.IsNullOrWhiteSpace(cfg.GameInstallPath))
        {
            var exePath = Path.Combine(cfg.GameInstallPath, "SlayTheSpire2.exe");
            if (!File.Exists(exePath))
            {
                Send(IpcResponse.Error("INIT_CONFIG",
                    $"游戏安装路径无效：未找到 SlayTheSpire2.exe\n请确认路径是否正确：{cfg.GameInstallPath}"));
                return;
            }
        }

        // validate save folder: must contain profile1/ dir and profile.save file
        if (!string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            var profileDir = Path.Combine(cfg.SaveFolderPath, "profile1");
            var profileSave = Path.Combine(cfg.SaveFolderPath, "profile.save");
            if (!Directory.Exists(profileDir) || !File.Exists(profileSave))
            {
                Send(IpcResponse.Error("INIT_CONFIG",
                    $"存档路径无效：未找到 profile1 文件夹或 profile.save 文件\n请确认路径是否正确：{cfg.SaveFolderPath}"));
                return;
            }
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

        Send(IpcResponse.Progress("INIT_CONFIG", "正在克隆仓库，请稍候..."));

        // clone if repo doesn't exist yet
        if (!_configService.IsRepoInitialized)
        {
            if (Directory.Exists(_configService.RepoPath))
                Directory.Delete(_configService.RepoPath, true);
            if (Directory.Exists(_configService.GitDirPath))
                Directory.Delete(_configService.GitDirPath, true);

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

        // flatten BranchInfo to plain objects so JSON stays predictable
        var list = branches.Select(b => new
        {
            name = b.Name,
            author = b.Author,
            lastModified = b.LastModified.ToUnixTimeMilliseconds()
        });

        Send(IpcResponse.Success("GET_BRANCHES", new { branches = list, currentBranch = current }));
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
        if (_gitService.IsOnInitBranch)
        {
            Send(IpcResponse.Error("SAVE_AND_PUSH_MY_BRANCH", "请先选择或创建一个分支"));
            return;
        }

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

    private void HandleOpenFolder(JsonElement? payload)
    {
        var folderType = payload?.GetProperty("folderType").GetString();
        var cfg = _configService.LoadConfig();

        var path = folderType switch
        {
            "mod" => cfg.GameModPath,
            "save" => cfg.SaveFolderPath,
            "config" => ConfigService.AppDataDirPath,
            "backup" => SaveBackupService.BackupDir,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Send(IpcResponse.Error("OPEN_FOLDER", "文件夹路径不存在或未配置"));
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = false
        });

        Send(IpcResponse.Success("OPEN_FOLDER"));
    }

    /// <summary>
    /// open native folder browser dialog, return selected path
    /// </summary>
    private void HandlePickFolder()
    {
        string? selectedPath = null;

        // FolderBrowserDialog must run on STA thread
        var tcs = new TaskCompletionSource<string?>();
        _uiContext.Post(_ =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择文件夹",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog(_form) == DialogResult.OK)
                selectedPath = dialog.SelectedPath;

            tcs.SetResult(selectedPath);
        }, null);

        var result = tcs.Task.GetAwaiter().GetResult();

        if (!string.IsNullOrWhiteSpace(result))
            Send(IpcResponse.Success("PICK_FOLDER", new { path = result }));
        else
            Send(IpcResponse.Success("PICK_FOLDER", new { path = (string?)null }));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private void EnsureJunction(string gameModPath)
    {
        if (_junctionService.IsJunction(gameModPath))
            return; // already good

        // backup existing real folder using the backup service
        if (Directory.Exists(gameModPath))
        {
            _backupService.BackupModFolder(gameModPath);
            Directory.Delete(gameModPath, true);
        }

        var ok = _junctionService.CreateJunction(gameModPath, _configService.RepoPath);
        if (!ok)
        {
            // fallback: copy files instead
            Send(IpcResponse.Progress("JUNCTION_FALLBACK", "Junction 创建失败，降级为复制模式..."));
            _junctionService.FallbackCopy(_configService.RepoPath, gameModPath);
        }
    }

    // ── save management handlers ─────────────────────────────────────

    private void HandleGetSaveStatus()
    {
        var cfg = _configService.LoadConfig();

        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath) || !Directory.Exists(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Success("GET_SAVE_STATUS", new { isConfigured = false }));
            return;
        }

        var status = _mergeService.GetStatus(cfg.SaveFolderPath);

        var mergeState = status.IsFullyLinked ? "linked"
                       : status.IsPartiallyLinked ? "partial"
                       : status.HasModdedFolder ? "unlinked"
                       : "no_modded";

        Send(IpcResponse.Success("GET_SAVE_STATUS", new
        {
            isConfigured = true,
            mergeState,
            profiles = status.Profiles.Select(p => new
            {
                name = p.Name,
                normalExists = p.NormalExists,
                moddedExists = p.ModdedExists,
                isJunction = p.IsJunction
            })
        }));
    }

    private void HandleAnalyzeSaveMerge()
    {
        var cfg = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Error("ANALYZE_SAVE_MERGE", "存档路径未配置"));
            return;
        }

        var moddedDir = Path.Combine(cfg.SaveFolderPath, "modded");
        var hasRealModded = Directory.Exists(moddedDir) &&
            Directory.GetDirectories(moddedDir)
                .Any(d => !_junctionService.IsJunction(d));

        if (!hasRealModded)
        {
            // no comparison needed, can merge directly
            Send(IpcResponse.Success("ANALYZE_SAVE_MERGE", new { needsComparison = false }));
            return;
        }

        var comparisons = _mergeService.CompareProfiles(cfg.SaveFolderPath);

        Send(IpcResponse.Success("ANALYZE_SAVE_MERGE", new
        {
            needsComparison = true,
            profiles = comparisons.Select(c => new
            {
                name = c.Name,
                normal = c.Normal == null ? null : new
                {
                    sizeBytes = c.Normal.SizeBytes,
                    lastModified = new DateTimeOffset(c.Normal.LastModified).ToUnixTimeMilliseconds(),
                    fileCount = c.Normal.FileCount
                },
                modded = c.Modded == null ? null : new
                {
                    sizeBytes = c.Modded.SizeBytes,
                    lastModified = new DateTimeOffset(c.Modded.LastModified).ToUnixTimeMilliseconds(),
                    fileCount = c.Modded.FileCount
                },
                recommendation = c.Recommendation
            })
        }));
    }

    private void HandleExecuteSaveMerge(JsonElement? payload)
    {
        var cfg = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Error("EXECUTE_SAVE_MERGE", "存档路径未配置"));
            return;
        }

        Send(IpcResponse.Progress("EXECUTE_SAVE_MERGE", "正在备份并合并存档..."));

        // parse choices from payload (may be null for simple case)
        Dictionary<string, string>? choices = null;
        if (payload != null && payload.Value.TryGetProperty("choices", out var choicesEl))
        {
            choices = new Dictionary<string, string>();
            foreach (var prop in choicesEl.EnumerateObject())
                choices[prop.Name] = prop.Value.GetString() ?? "normal";
        }

        var backupPath = _mergeService.Merge(cfg.SaveFolderPath, choices);

        Send(IpcResponse.Success("EXECUTE_SAVE_MERGE", new
        {
            message = "存档合并完成！操作前已自动备份。",
            backupName = Path.GetFileName(backupPath)
        }));
    }

    private void HandleUnlinkSaves()
    {
        var cfg = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Error("UNLINK_SAVES", "存档路径未配置"));
            return;
        }

        Send(IpcResponse.Progress("UNLINK_SAVES", "正在取消合并..."));

        var backupPath = _mergeService.Unlink(cfg.SaveFolderPath);

        Send(IpcResponse.Success("UNLINK_SAVES", new
        {
            message = "存档已取消合并，Mod 存档恢复为独立副本。",
            backupName = Path.GetFileName(backupPath)
        }));
    }

    private void HandleBackupSaves()
    {
        var cfg = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Error("BACKUP_SAVES", "存档路径未配置"));
            return;
        }

        Send(IpcResponse.Progress("BACKUP_SAVES", "正在备份存档..."));

        var backupPath = _backupService.BackupSaveFolder(cfg.SaveFolderPath);

        Send(IpcResponse.Success("BACKUP_SAVES", new
        {
            message = $"存档已备份到 {Path.GetFileName(backupPath)}"
        }));
    }

    private void HandleGetBackupList()
    {
        var backups = _backupService.ListBackups();

        Send(IpcResponse.Success("GET_BACKUP_LIST", new
        {
            backups = backups.Select(b => new
            {
                name = b.Name,
                createdAt = new DateTimeOffset(b.CreatedAt).ToUnixTimeMilliseconds(),
                sizeBytes = b.SizeBytes,
                type = b.Type
            })
        }));
    }

    private void HandleRestoreBackup(JsonElement? payload)
    {
        var backupName = payload?.GetProperty("backupName").GetString();
        if (string.IsNullOrWhiteSpace(backupName))
        {
            Send(IpcResponse.Error("RESTORE_BACKUP", "未指定备份名称"));
            return;
        }

        // sanitize: prevent path traversal (same check as DeleteBackup)
        if (backupName.Contains("..") || backupName.Contains('/') || backupName.Contains('\\'))
        {
            Send(IpcResponse.Error("RESTORE_BACKUP", "备份名称无效"));
            return;
        }

        var cfg = _configService.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.SaveFolderPath))
        {
            Send(IpcResponse.Error("RESTORE_BACKUP", "存档路径未配置"));
            return;
        }

        var backupPath = Path.Combine(SaveBackupService.BackupDir, backupName);
        if (!Directory.Exists(backupPath))
        {
            Send(IpcResponse.Error("RESTORE_BACKUP", "备份不存在或已被删除"));
            return;
        }

        Send(IpcResponse.Progress("RESTORE_BACKUP", "正在备份当前存档并恢复..."));

        // auto-backup current state before restoring
        _backupService.BackupSaveFolder(cfg.SaveFolderPath);

        _backupService.RestoreSaveBackup(backupPath, cfg.SaveFolderPath, _junctionService);

        Send(IpcResponse.Success("RESTORE_BACKUP", new
        {
            message = "存档已恢复！恢复前的状态已自动备份。"
        }));
    }

    private void HandleDeleteBackup(JsonElement? payload)
    {
        var backupName = payload?.GetProperty("backupName").GetString();
        if (string.IsNullOrWhiteSpace(backupName))
        {
            Send(IpcResponse.Error("DELETE_BACKUP", "未指定备份名称"));
            return;
        }

        _backupService.DeleteBackup(backupName);

        Send(IpcResponse.Success("DELETE_BACKUP", new
        {
            message = "备份已删除"
        }));
    }

    private void Send(IpcResponse response)
    {
        var json = response.ToJson();
        // PostWebMessageAsString must be called on the UI thread
        _uiContext.Post(_ => _webView.PostWebMessageAsString(json), null);
    }
}
