using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

public class ModManagerHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly GitService _gitService;
    private readonly ModInstallService _installService;
    private readonly MainForm _form;

    public ModManagerHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        GitService gitService,
        ModInstallService installService,
        MainForm form)
        : base(webView, uiContext)
    {
        _configService = configService;
        _gitService = gitService;
        _installService = installService;
        _form = form;
    }

    /// <summary>
    /// scan local mods with full detail + dependency analysis
    /// </summary>
    public void HandleGetLocalModsDetailed()
    {
        var allMods = _gitService.GetLocalModsDetailed();
        var (mods, ghosts) = GitService.AnalyzeDependencies(allMods);

        Send(IpcResponse.Success("GET_LOCAL_MODS_DETAILED", new
        {
            mods = mods.Select(m => new
            {
                id = m.Id,
                name = m.Name,
                author = m.Author,
                description = m.Description,
                version = m.Version,
                dependencies = m.Dependencies,
                hasDll = m.HasDll,
                hasPck = m.HasPck,
                folderName = m.FolderName,
                files = m.Files,
                sizeBytes = m.SizeBytes,
                missingFiles = m.MissingFiles,
                dependedBy = m.DependedBy,
            }),
            ghosts = ghosts.Select(g => new
            {
                id = g.Id,
                name = g.Name,
                dependedBy = g.DependedBy,
            }),
        }));
    }

    public void HandleDeleteMod(JsonElement? payload)
    {
        string? folderName = null;
        if (payload is not null && payload.Value.TryGetProperty("folderName", out var fnEl))
            folderName = fnEl.GetString();

        if (string.IsNullOrWhiteSpace(folderName))
        {
            Send(IpcResponse.Error("DELETE_MOD", "缺少文件夹名称"));
            return;
        }

        _gitService.DeleteModFolder(folderName);
        Send(IpcResponse.Success("DELETE_MOD", new { message = $"已删除 MOD：{folderName}" }));
    }

    /// <summary>
    /// open native file picker for mod archives (ZIP/RAR/7z), install selected archive
    /// </summary>
    public void HandlePickModArchive()
    {
        string? selectedPath = null;
        var tcs = new TaskCompletionSource<string?>();
        UiContext.Post(_ =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择 MOD 压缩包",
                Filter = "压缩文件 (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(_form) == DialogResult.OK)
                selectedPath = dialog.FileName;
            tcs.SetResult(selectedPath);
        }, null);

        var result = tcs.Task.GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(result))
        {
            Send(IpcResponse.Success("PICK_MOD_ARCHIVE", new { cancelled = true }));
            return;
        }

        try
        {
            var installed = _installService.InstallFromArchive(result, _configService.WorkTreePath);
            Send(IpcResponse.Success("PICK_MOD_ARCHIVE", new
            {
                cancelled = false,
                installed = installed,
            }));
        }
        catch (Exception ex)
        {
            Send(IpcResponse.Error("PICK_MOD_ARCHIVE", $"安装失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// install mod from file paths (drag-drop support)
    /// </summary>
    public void HandleInstallModFiles(JsonElement? payload)
    {
        var filePaths = new List<string>();
        if (payload is not null && payload.Value.TryGetProperty("filePaths", out var fpEl) && fpEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in fpEl.EnumerateArray())
            {
                var p = item.GetString();
                if (!string.IsNullOrWhiteSpace(p)) filePaths.Add(p);
            }
        }

        if (filePaths.Count == 0)
        {
            Send(IpcResponse.Error("INSTALL_MOD_FILES", "未提供文件路径"));
            return;
        }

        var allInstalled = new List<string>();

        foreach (var path in filePaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // drag-dropped a folder — copy directly
                    var folderName = Path.GetFileName(path);
                    var dest = Path.Combine(_configService.WorkTreePath, folderName);
                    CopyDirectory(path, dest);
                    allInstalled.Add(folderName);
                }
                else if (File.Exists(path))
                {
                    var installed = _installService.InstallFromArchive(path, _configService.WorkTreePath);
                    allInstalled.AddRange(installed);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to install from {path}: {ex.Message}");
            }
        }

        if (allInstalled.Count > 0)
            Send(IpcResponse.Success("INSTALL_MOD_FILES", new { installed = allInstalled }));
        else
            Send(IpcResponse.Error("INSTALL_MOD_FILES", "没有成功安装任何 MOD"));
    }

    /// <summary>
    /// list mods on a remote branch with folder name info, for the "copy from branch" feature
    /// </summary>
    public void HandleGetBranchModsForCopy(JsonElement? payload)
    {
        string? branchName = null;
        if (payload is not null && payload.Value.TryGetProperty("branchName", out var bnEl))
            branchName = bnEl.GetString();

        if (string.IsNullOrWhiteSpace(branchName))
        {
            Send(IpcResponse.Error("GET_BRANCH_MODS_FOR_COPY", "缺少分支名"));
            return;
        }

        var mods = _gitService.GetBranchModsForCopy(branchName);
        Send(IpcResponse.Success("GET_BRANCH_MODS_FOR_COPY", new
        {
            mods = mods.Select(m => new
            {
                id = m.Id,
                name = m.Name,
                author = m.Author,
                description = m.Description,
                version = m.Version,
                folderName = m.FolderName,
                dependencies = m.Dependencies,
            }),
        }));
    }

    public void HandleCopyModFromBranch(JsonElement? payload)
    {
        string? branchName = null, folderName = null;
        if (payload is not null)
        {
            if (payload.Value.TryGetProperty("branchName", out var bnEl)) branchName = bnEl.GetString();
            if (payload.Value.TryGetProperty("folderName", out var fnEl)) folderName = fnEl.GetString();
        }

        if (string.IsNullOrWhiteSpace(branchName) || string.IsNullOrWhiteSpace(folderName))
        {
            Send(IpcResponse.Error("COPY_MOD_FROM_BRANCH", "缺少分支名或文件夹名"));
            return;
        }

        _gitService.CopyModFromBranch(branchName, folderName);
        Send(IpcResponse.Success("COPY_MOD_FROM_BRANCH", new { message = $"已从分支 {branchName} 拷贝 MOD：{folderName}" }));
    }

    /// <summary>
    /// install mods from drag-dropped items. handles three types:
    /// - archives: base64 → temp file → InstallFromArchive
    /// - folders: base64 entries with relative paths → write to WorkTreePath
    /// - loose files: find manifest → create folder by mod id → write files
    /// WebView2 can't expose file paths to JS, so everything comes as base64.
    /// </summary>
    public void HandleInstallModDropped(JsonElement? payload)
    {
        if (payload is null)
        {
            Send(IpcResponse.Error("INSTALL_MOD_DROPPED", "未提供文件数据"));
            return;
        }

        var allInstalled = new List<string>();
        var tempFiles = new List<string>();

        try
        {
            // 1) archives — decode to temp, run InstallFromArchive
            if (payload.Value.TryGetProperty("archives", out var archivesEl) && archivesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var arc in archivesEl.EnumerateArray())
                {
                    var name = arc.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                    var data = arc.TryGetProperty("data", out var dEl) ? dEl.GetString() : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(data)) continue;

                    var bytes = Convert.FromBase64String(data);
                    var tempPath = Path.Combine(Path.GetTempPath(), $"sts_mod_{Guid.NewGuid()}{Path.GetExtension(name)}");
                    File.WriteAllBytes(tempPath, bytes);
                    tempFiles.Add(tempPath);

                    try
                    {
                        var installed = _installService.InstallFromArchive(tempPath, _configService.WorkTreePath);
                        allInstalled.AddRange(installed);
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn($"Failed to install archive {name}: {ex.Message}");
                    }
                }
            }

            // 2) folders — write entries preserving directory structure
            if (payload.Value.TryGetProperty("folders", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var folder in foldersEl.EnumerateArray())
                {
                    var folderName = folder.TryGetProperty("name", out var fnEl) ? fnEl.GetString() : null;
                    if (string.IsNullOrEmpty(folderName)) continue;
                    if (!folder.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array) continue;

                    var destDir = Path.Combine(_configService.WorkTreePath, folderName);
                    int fileCount = 0;

                    foreach (var entry in entriesEl.EnumerateArray())
                    {
                        var path = entry.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
                        var data = entry.TryGetProperty("data", out var dEl) ? dEl.GetString() : null;
                        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(data)) continue;

                        // path traversal guard
                        var normalized = path.Replace('\\', '/');
                        if (normalized.Contains("..")) continue;

                        var fileDest = Path.Combine(destDir, normalized);
                        var resolved = Path.GetFullPath(fileDest);
                        if (!resolved.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase)) continue;

                        var fileDir = Path.GetDirectoryName(fileDest);
                        if (fileDir != null) Directory.CreateDirectory(fileDir);

                        File.WriteAllBytes(fileDest, Convert.FromBase64String(data));
                        fileCount++;
                    }

                    if (fileCount > 0)
                        allInstalled.Add(folderName);
                }
            }

            // 3) loose files — find manifest, create folder by mod id, dump all files in
            if (payload.Value.TryGetProperty("looseFiles", out var looseEl) && looseEl.ValueKind == JsonValueKind.Array)
            {
                var looseItems = new List<(string Name, byte[] Bytes)>();
                string? modId = null;
                string? modName = null;

                foreach (var lf in looseEl.EnumerateArray())
                {
                    var name = lf.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                    var data = lf.TryGetProperty("data", out var dEl) ? dEl.GetString() : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(data)) continue;

                    var bytes = Convert.FromBase64String(data);
                    looseItems.Add((name, bytes));

                    // try to find a manifest among the loose files
                    if (modId == null && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var mod = JsonSerializer.Deserialize<ModInfo>(bytes, _jsonOpts);
                            if (!string.IsNullOrEmpty(mod?.Id))
                            {
                                modId = mod.Id;
                                modName = mod.Name ?? mod.Id;
                            }
                        }
                        catch { /* not a manifest */ }
                    }
                }

                if (looseItems.Count > 0)
                {
                    if (string.IsNullOrEmpty(modId))
                    {
                        LogService.Warn("Loose files dropped without a valid manifest json");
                        Send(IpcResponse.Error("INSTALL_MOD_DROPPED", "散落文件中未找到有效的 MOD 定义文件（需要包含 id 字段的 .json）"));
                        return;
                    }

                    var destDir = Path.Combine(_configService.WorkTreePath, modId);
                    Directory.CreateDirectory(destDir);

                    foreach (var (name, bytes) in looseItems)
                    {
                        var fileDest = Path.Combine(destDir, name);
                        File.WriteAllBytes(fileDest, bytes);
                    }

                    allInstalled.Add(modName!);
                }
            }

            if (allInstalled.Count > 0)
                Send(IpcResponse.Success("INSTALL_MOD_DROPPED", new { installed = allInstalled }));
            else
                Send(IpcResponse.Error("INSTALL_MOD_DROPPED", "没有成功安装任何 MOD"));
        }
        finally
        {
            // clean up temp archive files
            foreach (var tmp in tempFiles)
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(dest, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }
}
