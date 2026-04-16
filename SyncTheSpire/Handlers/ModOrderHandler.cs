using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using SyncTheSpire.Adapters;
using SyncTheSpire.Models;
using SyncTheSpire.Services;

namespace SyncTheSpire.Handlers;

/// <summary>
/// Handles mod load order operations — reads/writes mod_list in settings.save.
/// STS2-specific: the game loads mods in the order they appear in mod_settings.mod_list.
/// </summary>
public class ModOrderHandler : HandlerBase
{
    private readonly ConfigService _configService;
    private readonly GitService _gitService;
    private readonly IGameAdapter _adapter;

    public ModOrderHandler(
        CoreWebView2 webView,
        SynchronizationContext uiContext,
        ConfigService configService,
        GitService gitService,
        IGameAdapter adapter)
        : base(webView, uiContext)
    {
        _configService = configService;
        _gitService = gitService;
        _adapter = adapter;
    }

    /// <summary>
    /// Read mod_list from settings.save, enrich with mod metadata from installed mods,
    /// and check consistency between mod_list and actually installed mods.
    /// </summary>
    public void HandleGetModOrder()
    {
        if (!_adapter.SupportsModOrder)
        {
            Send(IpcResponse.Error("GET_MOD_ORDER", "当前游戏不支持 Mod 排序"));
            return;
        }

        var savePath = _configService.Workspace.SaveFolderPath;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            Send(IpcResponse.Error("GET_MOD_ORDER", "未配置存档文件夹，请先在设置中指定存档路径"));
            return;
        }

        var settingsFile = Path.Combine(savePath, "settings.save");
        if (!File.Exists(settingsFile))
        {
            Send(IpcResponse.Error("GET_MOD_ORDER", "未找到 settings.save，请先启动一次游戏"));
            return;
        }

        try
        {
            var json = File.ReadAllText(settingsFile);
            var root = JsonNode.Parse(json);
            var modList = root?["mod_settings"]?["mod_list"]?.AsArray();
            var modsEnabled = root?["mod_settings"]?["mods_enabled"]?.GetValue<bool>() ?? false;

            if (modList is null)
            {
                Send(IpcResponse.Error("GET_MOD_ORDER", "settings.save 中未找到 mod_list，请先启动一次游戏"));
                return;
            }

            // build lookup from installed mods (filesystem)
            var installedMods = _gitService.GetLocalMods();
            var modLookup = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in installedMods)
            {
                if (!string.IsNullOrEmpty(m.Id))
                    modLookup.TryAdd(m.Id, m);
            }

            // consistency check: only compare mods_directory entries against installed mods
            var listIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in modList)
            {
                var source = item?["source"]?.GetValue<string>();
                var id = item?["id"]?.GetValue<string>();
                if (source == "mods_directory" && !string.IsNullOrEmpty(id))
                    listIds.Add(id);
            }

            var installedIds = new HashSet<string>(modLookup.Keys, StringComparer.OrdinalIgnoreCase);
            var consistent = listIds.SetEquals(installedIds);

            // build enriched mod list for frontend
            var mods = new List<object>();
            foreach (var item in modList)
            {
                var id = item?["id"]?.GetValue<string>() ?? "";
                var isEnabled = item?["is_enabled"]?.GetValue<bool>() ?? false;
                var source = item?["source"]?.GetValue<string>() ?? "";

                modLookup.TryGetValue(id, out var info);
                mods.Add(new
                {
                    id,
                    isEnabled,
                    source,
                    name = info?.Name ?? id,
                    author = info?.Author ?? "",
                    version = info?.Version ?? "",
                    description = info?.Description ?? "",
                });
            }

            Send(IpcResponse.Success("GET_MOD_ORDER", new { mods, consistent, modsEnabled }));
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to read mod order", ex);
            Send(IpcResponse.Error("GET_MOD_ORDER", $"读取 Mod 排序失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// Reorder mod_list in settings.save according to the frontend-provided order.
    /// Preserves all other fields in the JSON.
    /// </summary>
    public void HandleSaveModOrder(JsonElement? payload)
    {
        if (payload is null)
        {
            Send(IpcResponse.Error("SAVE_MOD_ORDER", "Missing payload"));
            return;
        }

        string[]? orderedIds = null;
        if (payload.Value.TryGetProperty("orderedIds", out var idsEl))
        {
            orderedIds = idsEl.EnumerateArray()
                .Select(e => e.GetString()!)
                .ToArray();
        }

        if (orderedIds is null || orderedIds.Length == 0)
        {
            Send(IpcResponse.Error("SAVE_MOD_ORDER", "排序列表为空"));
            return;
        }

        var savePath = _configService.Workspace.SaveFolderPath;
        var settingsFile = Path.Combine(savePath, "settings.save");

        if (!File.Exists(settingsFile))
        {
            Send(IpcResponse.Error("SAVE_MOD_ORDER", "未找到 settings.save"));
            return;
        }

        try
        {
            var json = File.ReadAllText(settingsFile);
            var root = JsonNode.Parse(json);
            var modList = root?["mod_settings"]?["mod_list"]?.AsArray();

            if (modList is null)
            {
                Send(IpcResponse.Error("SAVE_MOD_ORDER", "settings.save 中未找到 mod_list"));
                return;
            }

            // detach all entries from the array and index by id
            var entries = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in modList.ToList())
            {
                var id = item?["id"]?.GetValue<string>();
                if (id is null) continue;
                modList.Remove(item);
                entries[id] = item!;
            }

            // rebuild in the requested order
            var newList = new JsonArray();
            foreach (var id in orderedIds)
            {
                if (entries.Remove(id, out var node))
                    newList.Add(node);
            }
            // append any leftover entries not in the ordered list (defensive)
            foreach (var node in entries.Values)
                newList.Add(node);

            root!["mod_settings"]!["mod_list"] = newList;

            // write back with indentation to match the game's output format
            var writeOpts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsFile, root.ToJsonString(writeOpts));

            Send(IpcResponse.Success("SAVE_MOD_ORDER", new { message = "Mod 排序已保存" }));
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to save mod order", ex);
            Send(IpcResponse.Error("SAVE_MOD_ORDER", $"保存 Mod 排序失败：{ex.Message}"));
        }
    }
}
