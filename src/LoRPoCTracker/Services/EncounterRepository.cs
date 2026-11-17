using System.IO;
using System.Text.Json;
using LoRPoCTracker.Config;
using LoRPoCTracker.Models;

namespace LoRPoCTracker.Services;

/// <summary>
/// 关卡牌库录制仓库：把每个 Boss 在多局中出过的牌合并保存，
/// 后续遭遇时用"已录制牌库 - 本局已出牌"推算剩余牌库。
/// </summary>
public sealed class EncounterRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path = Path.Combine(AppPaths.DataDir, "encounters.json");

    /// <summary>本地录制数据，唯一写入目标，落盘到 encounters.json。</summary>
    private EncounterDatabaseFile _data = new();

    /// <summary>当前展示视图：本地源时与 _data.Encounters 同一引用，网络源时指向只读合并结果。</summary>
    private IReadOnlyDictionary<string, EncounterRecord> _view;
    private bool _localView = true;

    public EncounterRepository()
    {
        Load();
        _view = _data.Encounters;
    }

    /// <summary>当前是否为本地源视图（网络源只读，不允许录制/编辑/定档）。</summary>
    public bool IsLocalView => _localView;

    /// <summary>当前视图名称，用于菜单显示。</summary>
    public string ViewSourceName { get; private set; } = "本地录制";

    /// <summary>切回本地录制视图。</summary>
    public void UseLocalView()
    {
        _view = _data.Encounters;
        _localView = true;
        ViewSourceName = "本地录制";
    }

    /// <summary>切换到网络源的只读视图；外部数据不会写回 encounters.json。</summary>
    public void UseExternalView(IReadOnlyDictionary<string, EncounterRecord> view, string sourceName)
    {
        _view = view;
        _localView = false;
        ViewSourceName = sourceName;
    }

    public IReadOnlyDictionary<string, EncounterRecord> All => _view;

    /// <summary>本地录制（不随只读网络视图变化），导入 / 云源合并的读来源。</summary>
    public IReadOnlyDictionary<string, EncounterRecord> AllLocal => _data.Encounters;

    public EncounterRecord? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _view.TryGetValue(key, out var record) ? record : null;
    }

    /// <summary>按 key 取本地录制（当前视图可能是只读网络源）。</summary>
    public EncounterRecord? GetLocal(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var actual = ResolveLocalKey(key.Trim());
        return actual is not null && _data.Encounters.TryGetValue(actual, out var record) ? record : null;
    }

    /// <summary>
    /// 把一批牌库整体写入本地录制（导入单个牌库 / 网络源合并共用）：
    /// 卡牌与衍生标记整体替换，归档状态只升不降；只落盘一次。
    /// </summary>
    public int WriteLocalDecks(IEnumerable<EncounterRecord> decks)
    {
        var written = 0;

        foreach (var deck in decks)
        {
            if (deck is null || string.IsNullOrWhiteSpace(deck.Key)) continue;

            var key = ResolveLocalKey(deck.Key.Trim()) ?? deck.Key.Trim();
            if (!_data.Encounters.TryGetValue(key, out var target))
            {
                target = new EncounterRecord
                {
                    Key = key,
                    FirstSeen = deck.FirstSeen == default ? DateTime.Now : deck.FirstSeen
                };
                _data.Encounters[key] = target;
            }

            ApplyDeck(target, deck);
            written++;
        }

        if (written > 0) Save();
        return written;
    }

    /// <summary>
    /// 旧数据读取后字典可能变成大小写敏感，这里按忽略大小写找一次真实键，
    /// 避免导入时因大小写差异产生重复关卡。
    /// </summary>
    private string? ResolveLocalKey(string key)
    {
        if (_data.Encounters.ContainsKey(key)) return key;

        foreach (var existing in _data.Encounters.Keys)
        {
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)) return existing;
        }

        return null;
    }

    /// <summary>整体替换牌库内容；名称只在导入方向有值时才覆盖，归档状态只升不降。</summary>
    private static void ApplyDeck(EncounterRecord target, EncounterRecord deck)
    {
        if (!string.IsNullOrWhiteSpace(deck.Campaign)) target.Campaign = deck.Campaign.Trim();
        if (!string.IsNullOrWhiteSpace(deck.MapName)) target.MapName = deck.MapName.Trim();
        if (!string.IsNullOrWhiteSpace(deck.NodeName)) target.NodeName = deck.NodeName.Trim();

        target.ExpectedCount = Math.Max(0, deck.ExpectedCount);
        target.IsFinalized |= deck.IsFinalized;
        target.LastSeen = deck.LastSeen == default ? DateTime.Now : deck.LastSeen;

        target.TokenCards = (deck.TokenCards ?? new List<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        target.Cards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, count) in deck.Cards ?? new Dictionary<string, int>())
        {
            if (string.IsNullOrWhiteSpace(code) || count <= 0) continue;
            if (target.IsToken(code)) continue;
            target.Cards[code.Trim()] = count;
        }
    }

    /// <summary>合并一局观察结果：同代码取历史最大值，避免多局累加导致虚高；定档牌库不再自动录制。</summary>
    public EncounterRecord Commit(string key, string campaign, string mapName, string nodeName,
        IReadOnlyDictionary<string, int> observed)
    {
        if (!_data.Encounters.TryGetValue(key, out var record))
        {
            record = new EncounterRecord
            {
                Key = key,
                FirstSeen = DateTime.Now
            };
            _data.Encounters[key] = record;
        }

        // 已定档：牌库由管理员手动维护，跳过本局的自动合并（编辑器写入不受影响）
        if (record.IsFinalized) return record;

        if (!string.IsNullOrWhiteSpace(campaign)) record.Campaign = campaign;
        if (!string.IsNullOrWhiteSpace(mapName)) record.MapName = mapName;
        if (!string.IsNullOrWhiteSpace(nodeName)) record.NodeName = nodeName;

        record.LastSeen = DateTime.Now;

        foreach (var (code, count) in observed)
        {
            if (string.IsNullOrWhiteSpace(code) || count <= 0) continue;
            if (record.IsToken(code)) continue;
            var current = record.Cards.GetValueOrDefault(code);
            if (count > current) record.Cards[code] = count;
        }

        Save();
        return record;
    }

    public void Clear(string key)
    {
        if (_data.Encounters.Remove(key)) Save();
    }

    /// <summary>确保存在该关卡的录制（用于对局中手动编辑）。</summary>
    public EncounterRecord Ensure(string key, string campaign, string mapName, string nodeName)
    {
        if (_data.Encounters.TryGetValue(key, out var record)) return record;

        record = new EncounterRecord
        {
            Key = key,
            Campaign = campaign,
            MapName = mapName,
            NodeName = nodeName,
            FirstSeen = DateTime.Now,
            LastSeen = DateTime.Now
        };
        _data.Encounters[key] = record;
        Save();
        return record;
    }

    /// <summary>
    /// 旧版录制只用 Boss key 作键；首次在新的组合 key 下遇到同一 Boss 时，
    /// 把旧记录迁移过来，保留已有的牌库。
    /// </summary>
    public EncounterRecord? AdoptLegacy(string legacyKey, string newKey, string campaign, string mapName, string nodeName)
    {
        if (string.Equals(legacyKey, newKey, StringComparison.Ordinal)) return null;
        if (!_data.Encounters.TryGetValue(legacyKey, out var record)) return null;
        if (_data.Encounters.ContainsKey(newKey)) return null;

        _data.Encounters.Remove(legacyKey);
        record.Key = newKey;
        if (!string.IsNullOrWhiteSpace(campaign)) record.Campaign = campaign;
        if (!string.IsNullOrWhiteSpace(mapName)) record.MapName = mapName;
        if (!string.IsNullOrWhiteSpace(nodeName)) record.NodeName = nodeName;
        _data.Encounters[newKey] = record;
        Save();
        return record;
    }

    /// <summary>手动设置某张牌的数量，0 表示删除。</summary>
    public void SetCard(string key, string code, int count)
    {
        if (!_data.Encounters.TryGetValue(key, out var record)) return;

        if (count <= 0)
        {
            record.Cards.Remove(code);
        }
        else
        {
            record.Cards[code] = count;
        }

        Save();
    }

    /// <summary>手动设置预定牌库总张数（0 表示清除）。</summary>
    public void SetExpectedCount(string key, int count)
    {
        if (!_data.Encounters.TryGetValue(key, out var record)) return;

        record.ExpectedCount = Math.Max(0, count);
        Save();
    }

    /// <summary>定档 / 取消定档牌库：定档后该关卡不再接受自动录制，但仍可手动编辑。</summary>
    public void SetFinalized(string key, bool finalized)
    {
        if (!_data.Encounters.TryGetValue(key, out var record)) return;
        if (record.IsFinalized == finalized) return;

        record.IsFinalized = finalized;
        Save();
    }

    /// <summary>
    /// 标记/取消标记衍生卡：衍生卡不写入录制，并从已有录制中移除。
    /// </summary>
    public void SetToken(string key, string code, bool isToken)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        if (!_data.Encounters.TryGetValue(key, out var record)) return;

        var normalized = code.Trim();
        if (isToken)
        {
            if (!record.IsToken(normalized)) record.TokenCards.Add(normalized);
            record.Cards.Remove(normalized);
        }
        else
        {
            record.TokenCards.RemoveAll(item =>
                string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }

        Save();
    }

    /// <summary>手动设置战役名与地图名。</summary>
    public void SetNames(string key, string campaign, string mapName)
    {
        if (!_data.Encounters.TryGetValue(key, out var record)) return;

        record.Campaign = campaign?.Trim() ?? "";
        record.MapName = mapName?.Trim() ?? "";
        Save();
    }

    /// <summary>清除录制中指定的卡（例如衍生物）。</summary>
    public int Prune(Func<string, bool> shouldRemove)
    {
        var removed = 0;
        foreach (var record in _data.Encounters.Values)
        {
            // 定档牌库只由管理员手动编辑，自动清理不得改动
            if (record.IsFinalized) continue;

            foreach (var code in record.Cards.Keys.Where(shouldRemove).ToList())
            {
                record.Cards.Remove(code);
                removed++;
            }
        }

        if (removed > 0) Save();
        return removed;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<EncounterDatabaseFile>(json, JsonOptions);
            if (data is not null)
            {
                _data = data;
            }
        }
        catch
        {
            _data = new EncounterDatabaseFile();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
        catch
        {
            // 保存失败不影响对局
        }
    }
}
