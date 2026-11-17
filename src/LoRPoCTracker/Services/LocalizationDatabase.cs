using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoRPoCTracker.Services;

/// <summary>
/// 解析游戏自带的本地化文本（LocalizedText_{locale}.bin），
/// 用于把 card_xxx_Name、Villain_Adventure_SH_Boss_Name 之类的 key 还原成中文名称。
/// 文件格式：uint16 长度 + UTF-8 字符串，键值成对出现。
/// </summary>
public sealed class LocalizationDatabase
{
    private readonly string _locale;
    private readonly string? _gameDir;
    private readonly string _cachePath;
    private Dictionary<string, string> _map = new(StringComparer.Ordinal);

    private static readonly Regex MarkupRegex = new("<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex DigitSuffixRegex = new(@"\d+$", RegexOptions.Compiled);

    public LocalizationDatabase(string locale, string? gameDir)
    {
        _locale = string.IsNullOrWhiteSpace(locale) ? "zh_tw" : locale;
        _gameDir = gameDir;
        _cachePath = Path.Combine(Config.AppPaths.DataDir, $"loc_{_locale}.json");
    }

    public int Count => _map.Count;
    public bool IsLoaded => _map.Count > 0;

    private List<(string Code, string Name)>? _cardNames;

    /// <summary>全部卡牌代码 → 名称（含 Data Dragon 没有的新卡），供搜索兜底。</summary>
    public IReadOnlyList<(string Code, string Name)> GetCardNames()
    {
        if (_cardNames is not null) return _cardNames;

        var list = new List<(string, string)>();
        foreach (var (key, value) in _map)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!key.StartsWith("card_", StringComparison.Ordinal)) continue;
            if (!key.EndsWith("_Name", StringComparison.Ordinal)) continue;

            var code = key[5..^5];
            if (code.Length == 0 || code.Contains('_')) continue;

            var name = CleanText(value);
            if (name.Length > 0) list.Add((code, name));
        }

        _cardNames = list;
        return list;
    }

    public string? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    public string? GetCardName(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return Get($"card_{code}_Name");
    }

    /// <summary>
    /// 战役（模式）名称，用于战役下拉框。
    /// 游戏里的战役名来自各模式的 mode_map_layer 文本，如 世界冒險、惡之道、奧術傳奇。
    /// </summary>
    public IReadOnlyList<string> GetCampaignNames()
    {
        var names = new List<string>();

        void Add(string key)
        {
            var name = Get(key);
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
        }

        Add("normal_mode_map_layer_name");        // 世界冒險
        Add("villain_mode_map_layer_name");       // 惡之道
        Add("eclair_mode_map_layer_name_2");      // 奧術傳奇
        Add("hard_mode_map_layer_name");          // 夢魘
        Add("chonkers_mode_map_layer_name");      // 泰坦的冒險
        Add("SpiritBlossom_mode_map_layer_name"); // 「靈花祭」冒險
        Add("TFT_mode_map_layer_name");           // 《聯盟戰棋》冒險
        Add("rogue_mode_map_layer_name");         // 符文大地之亂

        return names;
    }

    /// <summary>把冒险 key（如 Eclair_Adventure_Shurima_Name）归类到所属战役模式名；无法判断时返回 null。</summary>
    public string? GetCampaignName(string? adventureKey)
    {
        if (string.IsNullOrWhiteSpace(adventureKey)) return null;

        if (adventureKey.StartsWith("Villain_", StringComparison.OrdinalIgnoreCase))
            return Get("villain_mode_map_layer_name");
        if (adventureKey.StartsWith("Eclair_", StringComparison.OrdinalIgnoreCase))
            return Get("eclair_mode_map_layer_name_2");
        if (adventureKey.StartsWith("Chonkers_", StringComparison.OrdinalIgnoreCase))
            return Get("chonkers_mode_map_layer_name");
        if (adventureKey.StartsWith("SpiritBlossom_", StringComparison.OrdinalIgnoreCase))
            return Get("SpiritBlossom_mode_map_layer_name");
        if (adventureKey.StartsWith("TFT_", StringComparison.OrdinalIgnoreCase))
            return Get("TFT_mode_map_layer_name");
        if (adventureKey.StartsWith("Rogue_", StringComparison.OrdinalIgnoreCase))
            return Get("rogue_mode_map_layer_name");
        if (adventureKey.StartsWith("Hard_", StringComparison.OrdinalIgnoreCase))
            return Get("hard_mode_map_layer_name");

        // Power_4_*、XP1_* 等普通冒险属于世界冒險；Shared_Adventure_* 是统计文本，忽略
        if (adventureKey.StartsWith("Power_", StringComparison.OrdinalIgnoreCase) ||
            adventureKey.StartsWith("XP1_", StringComparison.OrdinalIgnoreCase) ||
            adventureKey.StartsWith("Normal_", StringComparison.OrdinalIgnoreCase))
            return Get("normal_mode_map_layer_name");

        return null;
    }

    /// <summary>战役 → 该战役拥有的大地图名称（用于大地图下拉框按战役联动过滤）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetMapsByCampaign()
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string? campaign, string? value)
        {
            var name = CleanText(value);
            if (string.IsNullOrWhiteSpace(campaign) || name.Length == 0) return;

            if (!grouped.TryGetValue(campaign, out var list))
            {
                grouped[campaign] = list = new List<string>();
            }

            if (!list.Contains(name)) list.Add(name);
        }

        // 各战役的冒险地图名，如 越獄追捕、蘇瑞瑪砂皇
        foreach (var pair in _map)
        {
            if (!IsAdventureMapKey(pair.Key)) continue;
            Add(GetCampaignName(pair.Key), pair.Value);
        }

        // 世界冒險里的英雄试炼大地图，如 迅捷斥候
        var normalCampaign = Get("normal_mode_map_layer_name");
        foreach (var pair in _map)
        {
            if (!pair.Key.StartsWith("Power_4_Champion_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!pair.Key.EndsWith("_Flavor", StringComparison.OrdinalIgnoreCase)) continue;
            Add(normalCampaign, pair.Value);
        }

        // 奧術傳奇的主线大地图，如 嗜血狂怒
        Add(Get("eclair_mode_map_layer_name_2"), Get("EclairAdv_title"));

        // 去掉带内部编号的重复项，如 愛歐尼亞01 / 比爾吉沃特03
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (campaign, list) in grouped)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var stripped = DigitSuffixRegex.Replace(list[i], "").Trim();
                if (stripped.Length > 0 && stripped != list[i] && list.Contains(stripped))
                {
                    list.RemoveAt(i);
                }
            }

            list.Sort(StringComparer.CurrentCulture);
            result[campaign] = list;
        }

        return result;
    }

    /// <summary>
    /// 世界冒险（英雄试炼）的大地图 key，如 Power_4_Champion_Teemo_Flavor（值是「迅捷斥候」）。
    /// 这类 key 不含 "_Adventure"，需要单独识别，否则切换到世界冒险后地图名会为空。
    /// </summary>
    public static bool IsWorldMapKey(string? key)
    {
        return !string.IsNullOrWhiteSpace(key) &&
               key.StartsWith("Power_4_Champion_", StringComparison.OrdinalIgnoreCase) &&
               key.EndsWith("_Flavor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是否是我们能用作「大地图」的配置 key（各战役冒险 / 世界冒险英雄试炼）。</summary>
    public static bool IsMapKey(string? key) => IsAdventureMapKey(key ?? "") || IsWorldMapKey(key);

    /// <summary>当前冒险 key（如 Eclair_Adventure_Shurima_Name）对应的大地图名；不是地图 key 时返回 null。</summary>
    public string? GetMapName(string? adventureKey)
    {
        if (!IsMapKey(adventureKey)) return null;

        var name = CleanText(Get(adventureKey));
        return name.Length == 0 ? null : name;
    }

    /// <summary>判断某个本地化 key 是否为冒险地图名（用于录制 key 与大地图判断）。</summary>
    public static bool IsAdventureMapKey(string key)
    {
        if (!key.Contains("_Adventure", StringComparison.OrdinalIgnoreCase)) return false;
        if (!key.EndsWith("_Name", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.Contains("_Boss_", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.Contains("Scores", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.Contains("Rerolls", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.StartsWith("Shared_Adventure", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.StartsWith("client_cmp_feature_unlock", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return MarkupRegex.Replace(value, "").Trim();
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var source = FindSource();
        if (source is null) return;

        try
        {
            var sourceTime = File.GetLastWriteTimeUtc(source);

            if (File.Exists(_cachePath) && File.GetLastWriteTimeUtc(_cachePath) >= sourceTime)
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false));
                if (cached is { Count: > 0 })
                {
                    _map = new Dictionary<string, string>(cached, StringComparer.Ordinal);
                    _cardNames = null;
                    return;
                }
            }

            var bytes = await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
            var parsed = Parse(bytes);
            if (parsed.Count == 0) return;

            _map = parsed;
            _cardNames = null;
            Directory.CreateDirectory(Config.AppPaths.DataDir);
            await File.WriteAllTextAsync(_cachePath,
                JsonSerializer.Serialize(_map), ct).ConfigureAwait(false);
        }
        catch
        {
            // 本地化读取失败时退回显示 key
        }
    }

    private string? FindSource()
    {
        var gameDir = GamePaths.DetectGameDir(_gameDir);
        if (gameDir is null) return null;

        var candidates = new[]
        {
            Path.Combine(gameDir, "PatcherData", "PatchableFiles", "GamePlayData",
                $"LocalizedText_{_locale}.bin"),
            Path.Combine(gameDir, "Game", "LoR_Data", "StreamingAssets", "EmbeddedGamePlayData",
                $"LocalizedText_{_locale}.bin")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static Dictionary<string, string> Parse(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var length = data.Length;

        for (var i = 0; i < length - 5; i++)
        {
            var keyLength = data[i] | (data[i + 1] << 8);
            if (keyLength is < 3 or > 160) continue;

            var keyStart = i + 2;
            if (keyStart + keyLength + 2 > length) continue;

            var valid = true;
            var hasSeparator = false;
            for (var j = 0; j < keyLength; j++)
            {
                var b = data[keyStart + j];
                if (b is < 32 or > 126)
                {
                    valid = false;
                    break;
                }

                if (b == (byte)'_' || b == (byte)'/' || b == (byte)'.') hasSeparator = true;
            }

            if (!valid || !hasSeparator) continue;

            var valuePos = keyStart + keyLength;
            var valueLength = data[valuePos] | (data[valuePos + 1] << 8);
            if (valueLength > 4000) continue;
            if (valuePos + 2 + valueLength > length) continue;

            var key = Encoding.ASCII.GetString(data, keyStart, keyLength);
            var value = Encoding.UTF8.GetString(data, valuePos + 2, valueLength);
            result[key] = value;

            i = valuePos + 2 + valueLength - 1;
        }

        return result;
    }
}
