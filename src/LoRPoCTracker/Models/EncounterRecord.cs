using System.Text.RegularExpressions;

namespace LoRPoCTracker.Models;

/// <summary>单个关卡（按 战役 + 大地图 + Boss 识别）的牌库录制。</summary>
public sealed class EncounterRecord
{
    /// <summary>
    /// 组合键：冒险 key|Boss 名称 key（如 Rogue_Adventure_Targon_Name|card_03MT018_Name）。
    /// 冒险 key 唯一对应「战役 + 大地图」，因此同名地图在不同战役下也不会混淆；
    /// 无法识别地图时退回纯 Boss key（兼容旧数据）。
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>Boss 名称 key（组合键最后一段），用于界面显示。</summary>
    public string BossKey => EncounterKeys.BossKeyOf(Key);

    /// <summary>战役名称（如 更大的邪惡）。</summary>
    public string Campaign { get; set; } = "";

    /// <summary>大地图/地区名称（如 闇影島）。</summary>
    public string MapName { get; set; } = "";

    /// <summary>地图节点名称（游戏内遭遇战名称）。</summary>
    public string NodeName { get; set; } = "";

    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>预定牌库总张数（0 表示未设置），用于与实际录制张数对比；对手会丢牌，打完可能对不上。</summary>
    public int ExpectedCount { get; set; }

    /// <summary>
    /// 定档牌库：牌库内容已人工确认，之后再到这个关卡不再自动录制（不合并出牌），
    /// 但仍可在「编辑 Boss 牌库」「关卡录制管理」中手动编辑。
    /// </summary>
    public bool IsFinalized { get; set; }

    /// <summary>多次遭遇中观察到的单局最大数量。</summary>
    public Dictionary<string, int> Cards { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>手动标记为衍生的卡牌代码：不写入录制，也不参与剩余牌库计算。</summary>
    public List<string> TokenCards { get; set; } = new();

    /// <summary>该卡是否被手动标记为衍生。</summary>
    public bool IsToken(string code) =>
        TokenCards.Any(item => string.Equals(item, code, StringComparison.OrdinalIgnoreCase));
}

public sealed class EncounterDatabaseFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, EncounterRecord> Encounters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class EncounterKeys
{
    private static readonly Regex KeyRegex = new(@"^card_([0-9A-Za-z]+)_Name$", RegexOptions.Compiled);

    /// <summary>
    /// 英雄之路的 OpponentName 形如 card_03MT018_Name，取出英雄卡代码作为关卡键。
    /// 普通 PvP 名字返回 null，不参与录制。
    /// </summary>
    public static string? FromOpponentName(string? opponentName)
    {
        if (string.IsNullOrWhiteSpace(opponentName)) return null;
        var match = KeyRegex.Match(opponentName.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>组合录制键：冒险 key|Boss key；没有冒险 key 时退回 Boss key（兼容旧数据）。</summary>
    public static string Compose(string? adventureKey, string bossKey)
    {
        return string.IsNullOrWhiteSpace(adventureKey) ? bossKey : $"{adventureKey}|{bossKey}";
    }

    /// <summary>取组合键中的 Boss 部分。</summary>
    public static string BossKeyOf(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        var index = key.LastIndexOf('|');
        return index >= 0 && index + 1 < key.Length ? key[(index + 1)..] : key;
    }
}
