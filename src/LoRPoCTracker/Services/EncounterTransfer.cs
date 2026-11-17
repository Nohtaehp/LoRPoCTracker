using System.IO;
using System.Text;
using System.Text.Json;
using LoRPoCTracker.Models;

namespace LoRPoCTracker.Services;

/// <summary>网络源合并到本地的计划与统计。</summary>
public sealed class DeckMergePlan
{
    /// <summary>需要写入本地的牌库（已按规则过滤）。</summary>
    public List<EncounterRecord> Writes { get; } = new();

    /// <summary>本地没有、将新增的条数。</summary>
    public int Added { get; set; }

    /// <summary>本地已有但未归档、将被覆盖的条数。</summary>
    public int Updated { get; set; }

    /// <summary>本地已归档、保持原样的条数。</summary>
    public int SkippedLocalArchived { get; set; }

    /// <summary>云端未归档（草稿）、不写入的条数。</summary>
    public int SkippedCloudDraft { get; set; }

    /// <summary>云端归档了但没有任何卡牌、不写入的条数。</summary>
    public int SkippedEmpty { get; set; }

    public string Message
    {
        get
        {
            var text = $"新增 {Added} 个、覆盖 {Updated} 个；" +
                       $"跳过：本地已归档 {SkippedLocalArchived} 个、云端未归档 {SkippedCloudDraft} 个";
            return SkippedEmpty > 0 ? text + $"、空牌库 {SkippedEmpty} 个" : text;
        }
    }
}

/// <summary>
/// 单个 Boss 牌库的导出 / 导入，以及把网络源整体增量合并到本地：
/// 合并只写入「云端已归档、本地未归档（或本地没有）」的牌库，本地已归档的不会被覆盖。
/// </summary>
public static class EncounterTransfer
{
    public const string FileExtension = ".lorboss.json";

    public const string FileFilter =
        "Boss 牌库文件 (*.lorboss.json)|*.lorboss.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>导出文件名：Boss/节点名优先，其次地图名，最后用卡牌代码。</summary>
    public static string BuildFileName(EncounterRecord record)
    {
        var name = FirstNotEmpty(record.NodeName, record.MapName, record.Campaign);
        if (name.Length == 0)
        {
            var code = EncounterKeys.FromOpponentName(record.BossKey);
            name = string.IsNullOrWhiteSpace(code) ? record.BossKey : code;
        }

        if (name.Length == 0) name = "boss-deck";

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => invalid.Contains(ch) || ch == '|' ? '_' : ch).ToArray()).Trim();
        if (safe.Length == 0) safe = "boss-deck";
        if (safe.Length > 80) safe = safe[..80];

        return safe + FileExtension;
    }

    /// <summary>把一份已归档的牌库写成单 Boss 导出文件。</summary>
    public static void Export(EncounterRecord record, string path)
    {
        var deck = Clone(record);
        // 只有归档（定档）后的牌库才允许导出，文件里固定标记为已归档
        deck.IsFinalized = true;
        deck.Key = deck.Key.Trim();

        var file = new EncounterDeckFile
        {
            ExportedAt = DateTime.Now,
            ExportedBy = typeof(EncounterTransfer).Assembly.GetName().Version?.ToString(3) ?? "",
            Encounter = deck
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions), new UTF8Encoding(false));
    }

    /// <summary>
    /// 读取单 Boss 牌库文件。兼容三种写法：本程序的导出文件、
    /// 只含一条录制的 encounters.json、以及单条 EncounterRecord。
    /// </summary>
    public static bool TryRead(string path, out EncounterRecord? record, out string message)
    {
        record = null;

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            message = $"无法读取文件：{ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            message = "文件是空的。";
            return false;
        }

        try
        {
            var file = JsonSerializer.Deserialize<EncounterDeckFile>(json, JsonOptions);
            if (file?.Encounter is not null)
            {
                record = file.Encounter;
            }
            else
            {
                // 也接受只有一条录制的整份 encounters.json
                var database = JsonSerializer.Deserialize<EncounterDatabaseFile>(json, JsonOptions);
                if (database?.Encounters is { Count: 1 } single)
                {
                    record = single.Values.First();
                }
                else
                {
                    var bare = JsonSerializer.Deserialize<EncounterRecord>(json, JsonOptions);
                    if (!string.IsNullOrWhiteSpace(bare?.Key)) record = bare;
                }
            }
        }
        catch (Exception ex)
        {
            message = $"文件不是有效的 Boss 牌库 json：{ex.Message}";
            return false;
        }

        if (record is null || string.IsNullOrWhiteSpace(record.Key))
        {
            message = "文件里没有可识别的 Boss 牌库（缺少 Encounter / Key）。";
            return false;
        }

        record = Normalize(record);
        var total = record.Cards.Values.Sum();
        if (record.Cards.Count == 0)
        {
            message = "文件里的牌库没有任何卡牌。";
            return false;
        }

        message = $"{record.Cards.Count} 种 / {total} 张" +
                  (record.IsFinalized ? " · 已归档" : " · 未归档");
        return true;
    }

    /// <summary>把牌库改挂到指定关卡键（导入到选中的 Boss）。</summary>
    public static EncounterRecord Retarget(EncounterRecord record, string key)
    {
        var copy = Normalize(record);
        if (!string.IsNullOrWhiteSpace(key)) copy.Key = key.Trim();
        return copy;
    }

    /// <summary>
    /// 网络源 → 本地：只取云端已归档、且本地未归档（含本地没有）的牌库；
    /// 本地已归档的牌库保持原样，云端未归档的草稿也不写入。
    /// </summary>
    public static DeckMergePlan PlanSourceMerge(
        IReadOnlyDictionary<string, EncounterRecord> cloud,
        IReadOnlyDictionary<string, EncounterRecord> local)
    {
        var plan = new DeckMergePlan();

        // 本地字典可能因旧数据读盘变成大小写敏感，先建忽略大小写的索引
        var localIndex = new Dictionary<string, EncounterRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in local)
        {
            if (!string.IsNullOrWhiteSpace(key)) localIndex[key] = value;
        }

        foreach (var source in cloud.Values)
        {
            if (string.IsNullOrWhiteSpace(source.Key)) continue;

            if (!source.IsFinalized)
            {
                plan.SkippedCloudDraft++;
                continue;
            }

            if (source.Cards is null || source.Cards.Count == 0)
            {
                plan.SkippedEmpty++;
                continue;
            }

            localIndex.TryGetValue(source.Key, out var existing);
            if (existing?.IsFinalized == true)
            {
                plan.SkippedLocalArchived++;
                continue;
            }

            var deck = Normalize(source);
            deck.IsFinalized = true;
            plan.Writes.Add(deck);

            if (existing is null) plan.Added++;
            else plan.Updated++;
        }

        return plan;
    }

    /// <summary>统一大小写不敏感的比较器、去掉非法数量与重复的衍生标记。</summary>
    public static EncounterRecord Normalize(EncounterRecord source)
    {
        var result = new EncounterRecord
        {
            Key = source.Key?.Trim() ?? "",
            Campaign = source.Campaign?.Trim() ?? "",
            MapName = source.MapName?.Trim() ?? "",
            NodeName = source.NodeName?.Trim() ?? "",
            FirstSeen = source.FirstSeen,
            LastSeen = source.LastSeen,
            ExpectedCount = Math.Max(0, source.ExpectedCount),
            IsFinalized = source.IsFinalized,
            Cards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            TokenCards = new List<string>()
        };

        foreach (var (code, count) in source.Cards ?? new Dictionary<string, int>())
        {
            if (string.IsNullOrWhiteSpace(code) || count <= 0) continue;
            result.Cards[code.Trim()] = count;
        }

        foreach (var token in source.TokenCards ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(token)) continue;

            var trimmed = token.Trim();
            if (result.TokenCards.Any(item =>
                    string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase))) continue;
            result.TokenCards.Add(trimmed);
        }

        return result;
    }

    private static EncounterRecord Clone(EncounterRecord source) => Normalize(source);

    private static string FirstNotEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }
}
