using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoRPoCTracker.Services;

/// <summary>
/// 从 GamePlayData.bin 建立 adventureConfigGuid → 战役名称 key 的映射。
/// 数据布局中，冒险记录里名称 key 位于 GUID 之前，取最近的 *_Adventure*_Name。
/// </summary>
public sealed class GameDataIndex
{
    private static readonly Regex GuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    /// <summary>
    /// 冒险名称 key：
    /// · 英雄之路各战役：Eclair_Adventure_Shurima_Name 之类；
    /// · 世界冒险（英雄试炼）大地图：Power_4_Champion_Teemo_Flavor（值如「迅捷斥候」）。
    /// </summary>
    private static readonly Regex CampaignKeyRegex = new(
        @"(?:[A-Za-z0-9_]{4,60}_Adventure[A-Za-z0-9_]*_Name|Power_4_Champion_[A-Za-z0-9]+_Flavor)",
        RegexOptions.Compiled);

    private readonly string? _gameDir;
    private readonly string _cachePath;
    private Dictionary<string, string> _campaignKeys = new(StringComparer.OrdinalIgnoreCase);

    public GameDataIndex(string? gameDir)
    {
        _gameDir = gameDir;
        _cachePath = Path.Combine(Config.AppPaths.DataDir, "campaigns.json");
    }

    public string? GetCampaignKey(string? adventureGuid)
    {
        if (string.IsNullOrWhiteSpace(adventureGuid)) return null;
        return _campaignKeys.TryGetValue(adventureGuid, out var key) ? key : null;
    }

    /// <summary>当前 guid → 名称 key 映射的快照（诊断 / 验证用）。</summary>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        new Dictionary<string, string>(_campaignKeys, StringComparer.OrdinalIgnoreCase);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var gameDir = GamePaths.DetectGameDir(_gameDir);
        if (gameDir is null) return;

        var source = Path.Combine(gameDir, "PatcherData", "PatchableFiles", "GamePlayData", "GamePlayData.bin");
        if (!File.Exists(source)) return;

        try
        {
            var sourceTime = File.GetLastWriteTimeUtc(source);
            if (File.Exists(_cachePath) && File.GetLastWriteTimeUtc(_cachePath) >= sourceTime)
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false));
                if (cached is { Count: > 0 })
                {
                    _campaignKeys = new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
                    return;
                }
            }

            var bytes = await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
            var text = Encoding.ASCII.GetString(bytes);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in GuidRegex.Matches(text))
            {
                var start = Math.Max(0, match.Index - 700);
                var window = text.Substring(start, match.Index - start);
                string? lastKey = null;
                foreach (Match keyMatch in CampaignKeyRegex.Matches(window))
                {
                    lastKey = keyMatch.Value;
                }

                if (lastKey is not null)
                {
                    map[match.Value] = lastKey;
                }
            }

            if (map.Count == 0) return;

            _campaignKeys = map;
            Directory.CreateDirectory(Config.AppPaths.DataDir);
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(map), ct).ConfigureAwait(false);
        }
        catch
        {
            // 映射失败时只是没有战役名，不影响其他功能
        }
    }
}
