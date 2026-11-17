namespace LoRPoCTracker.Models;

/// <summary>
/// 单个 Boss 牌库的导出文件（*.lorboss.json）：只装一个已归档的关卡录制，
/// 便于单独分发某个 Boss 的牌库，而不是整份 encounters.json。
/// </summary>
public sealed class EncounterDeckFile
{
    /// <summary>文件类型标记，用来识别并拒绝无关的 json。</summary>
    public const string KindValue = "lor-poc-boss-deck";

    public string Kind { get; set; } = KindValue;

    public int Version { get; set; } = 1;

    public DateTime ExportedAt { get; set; }

    /// <summary>导出方的程序版本，仅作追溯，不参与导入校验。</summary>
    public string ExportedBy { get; set; } = "";

    /// <summary>导出的单个关卡录制（Key 为 adventureKey|card_xxx_Name）。</summary>
    public EncounterRecord? Encounter { get; set; }
}

/// <summary>「Boss 牌库导出 / 导入」窗口中的一行，对应一份本地录制。</summary>
public sealed class DeckTransferRow
{
    public string Key { get; init; } = "";

    public string Campaign { get; init; } = "";

    public string MapName { get; init; } = "";

    public string NodeName { get; init; } = "";

    /// <summary>Boss 名称 key（组合键最后一段）。</summary>
    public string BossKey { get; init; } = "";

    public int CardCount { get; init; }

    public int CardTotal { get; init; }

    public bool IsFinalized { get; init; }

    public string ArchivedText => IsFinalized ? "已归档" : "未归档";

    public string DisplayName
    {
        get
        {
            var text = string.Join(" · ", new[] { Campaign, MapName, NodeName }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            return string.IsNullOrWhiteSpace(text) ? BossKey : text;
        }
    }
}
