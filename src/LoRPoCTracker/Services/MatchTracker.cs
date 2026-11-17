using LoRPoCTracker.Models;

namespace LoRPoCTracker.Services;

/// <summary>
/// 单局记牌状态机：
/// 以 /static-decklist 的牌库为基准，逐帧比对 /positional-rectangles 中出现的卡牌，
/// 把"第一次出现"的本地卡牌实例视为已抽到，从剩余牌库中扣除。
/// </summary>
public sealed class MatchTracker
{
    private readonly Dictionary<string, int> _initialDeck = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _remaining = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<int> _localInstanceIds = new();
    private readonly HashSet<int> _opponentInstanceIds = new();

    private readonly Dictionary<string, int> _localSeenByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _opponentPlayed = new(StringComparer.OrdinalIgnoreCase);

    // 对手牌按「实体牌/副本」统计：每张实体牌只扣一次，回手、死亡、复活都不会重复扣。
    // 同帧可见数量作为兜底，防止接口对副本复用 CardID 时少计。
    private readonly Dictionary<string, List<int>> _opponentIdsByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _maxConcurrentOpponent = new(StringComparer.OrdinalIgnoreCase);

    // 手动标记「已去除」的牌：对手丢弃/移除时 API 不记录，由玩家点右键扣 1 张（不写入录制）
    private readonly Dictionary<string, int> _manualRemoved = new(StringComparer.OrdinalIgnoreCase);

    // 接口若没有给出有效 CardID，本地牌库退化为「同一张牌的最大同时可见数」
    private readonly Dictionary<string, int> _maxConcurrentLocal = new(StringComparer.OrdinalIgnoreCase);

    private bool _idTrackingAvailable;
    private bool _idTrackingDecided;

    public string? PlayerName { get; private set; }
    public string? OpponentName { get; private set; }
    public string? DeckCode { get; private set; }
    public string? OpponentEncounterKey { get; private set; }
    public EncounterRecord? Encounter { get; private set; }

    public bool HasDeck => _initialDeck.Count > 0;
    public bool IsInGame { get; private set; }
    public bool DeckUnavailable { get; set; }
    public int OpponentHandCount { get; private set; }

    public int Revision { get; private set; } = 1;

    public IReadOnlyDictionary<string, int> InitialDeck => _initialDeck;
    public IReadOnlyDictionary<string, int> Remaining => _remaining;
    public IReadOnlyDictionary<string, int> OpponentPlayed => _opponentPlayed;
    public IReadOnlyDictionary<string, int> LocalSeen => _localSeenByCode;

    public int TotalInitial => _initialDeck.Values.Sum();
    public int TotalRemaining => _remaining.Values.Sum();
    public int TotalOpponentPlayed => _opponentPlayed.Values.Sum();

    public void Reset()
    {
        _initialDeck.Clear();
        _remaining.Clear();
        _localInstanceIds.Clear();
        _opponentInstanceIds.Clear();
        _localSeenByCode.Clear();
        _opponentPlayed.Clear();
        _opponentIdsByCode.Clear();
        _maxConcurrentOpponent.Clear();
        _maxConcurrentLocal.Clear();
        _manualRemoved.Clear();
        _idTrackingAvailable = false;
        _idTrackingDecided = false;
        PlayerName = null;
        OpponentName = null;
        DeckCode = null;
        OpponentEncounterKey = null;
        Encounter = null;
        IsInGame = false;
        DeckUnavailable = false;
        OpponentHandCount = 0;
        Revision++;
    }

    public void SetNames(string? player, string? opponent)
    {
        PlayerName = player;
        OpponentName = opponent;
    }

    /// <summary>绑定当前关卡的历史录制。</summary>
    public void SetEncounter(string? key, EncounterRecord? record)
    {
        OpponentEncounterKey = key;
        Encounter = record;
        Revision++;
    }

    /// <summary>
    /// Boss 剩余牌库 = 已录制牌库 - 本局已离开牌库的实体牌 - 手动标记「已去除」的牌。
    /// ignorePlayed 返回 true 的卡（如英雄法术）完全不扣；
    /// capPlayedAtOne 返回 true 的卡（如可回手反复打出的武器）最多只扣 1 张实体。
    /// </summary>
    public IReadOnlyDictionary<string, int> BuildBossRemaining(
        Func<string, bool>? ignorePlayed = null,
        Func<string, bool>? capPlayedAtOne = null)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (Encounter is null) return result;

        foreach (var (code, total) in Encounter.Cards)
        {
            var played = _opponentPlayed.GetValueOrDefault(code);
            if (ignorePlayed?.Invoke(code) == true) played = 0;
            else if (capPlayedAtOne?.Invoke(code) == true) played = Math.Min(played, 1);

            // 手动「已去除」不受上面两条规则影响，点一次扣一张
            var removed = _manualRemoved.GetValueOrDefault(code);

            var left = Math.Max(0, total - played - removed);
            if (left > 0) result[code] = left;
        }

        return result;
    }

    public int BossRecordedTotal => Encounter?.Cards.Values.Sum() ?? 0;

    public void ApplyStaticDeck(StaticDeck deck)
    {
        DeckCode = deck.DeckCode;
        _initialDeck.Clear();
        foreach (var (code, count) in deck.CardsInDeck)
        {
            if (string.IsNullOrWhiteSpace(code) || count <= 0) continue;
            _initialDeck[code] = _initialDeck.GetValueOrDefault(code) + count;
        }

        _remaining.Clear();
        foreach (var (code, count) in _initialDeck)
        {
            _remaining[code] = count;
        }

        // 牌库是在进入对局后拉取的，先出现的牌可能已经错过；用当前已见数量回填
        foreach (var (code, seen) in _localSeenByCode)
        {
            var used = Math.Min(seen, _remaining.GetValueOrDefault(code));
            if (used > 0) _remaining[code] -= used;
        }

        DeckUnavailable = false;
        Revision++;
    }

    /// <summary>应用一帧屏幕数据，返回是否有可见变化。</summary>
    public bool ApplySnapshot(GameSnapshot snapshot)
    {
        IsInGame = snapshot.IsInGame;

        var localCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var opponentCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var opponentHand = 0;
        var changed = false;

        foreach (var rect in snapshot.Rectangles)
        {
            if (!IsCardCode(rect.CardCode))
            {
                // "face" 是牌库堆，不属于可见手牌
                continue;
            }

            if (rect.LocalPlayer)
            {
                localCounts[rect.CardCode] = localCounts.GetValueOrDefault(rect.CardCode) + 1;

                if (rect.CardId > 0)
                {
                    DecideIdTracking(true);
                    if (_localInstanceIds.Add(rect.CardId))
                    {
                        _localSeenByCode[rect.CardCode] = _localSeenByCode.GetValueOrDefault(rect.CardCode) + 1;
                        if (_remaining.TryGetValue(rect.CardCode, out var left) && left > 0)
                        {
                            _remaining[rect.CardCode] = left - 1;
                        }

                        changed = true;
                    }
                }
            }
            else
            {
                opponentCounts[rect.CardCode] = opponentCounts.GetValueOrDefault(rect.CardCode) + 1;

                // 每个 CardID 只计一次：同一张实体牌回手/复活/再次打出不会重复扣牌库
                if (rect.CardId > 0 && _opponentInstanceIds.Add(rect.CardId))
                {
                    _opponentPlayed[rect.CardCode] = _opponentPlayed.GetValueOrDefault(rect.CardCode) + 1;
                    if (!_opponentIdsByCode.TryGetValue(rect.CardCode, out var ids))
                    {
                        ids = new List<int>();
                        _opponentIdsByCode[rect.CardCode] = ids;
                    }

                    ids.Add(rect.CardId);
                    changed = true;
                }
            }
        }

        if (snapshot.Rectangles.Any(r => r.CardId > 0 && IsCardCode(r.CardCode)))
        {
            DecideIdTracking(true);
        }
        else if (!_idTrackingDecided)
        {
            DecideIdTracking(false);
        }

        if (!_idTrackingAvailable)
        {
            ApplyConcurrentLocalFallback(localCounts);
            changed = true;
        }

        if (UpdateOpponentCounts(opponentCounts)) changed = true;

        if (opponentHand != OpponentHandCount)
        {
            OpponentHandCount = opponentHand;
            changed = true;
        }

        // 用名字做辅助判断，避免跨局残留
        if (!string.IsNullOrWhiteSpace(snapshot.PlayerName) && PlayerName is null) PlayerName = snapshot.PlayerName;
        if (!string.IsNullOrWhiteSpace(snapshot.OpponentName) && OpponentName is null) OpponentName = snapshot.OpponentName;

        if (changed) Revision++;
        return changed;
    }

    private static bool IsCardCode(string code)
    {
        return !string.IsNullOrWhiteSpace(code) &&
               !code.Equals("face", StringComparison.OrdinalIgnoreCase);
    }

    private void DecideIdTracking(bool available)
    {
        if (_idTrackingDecided) return;
        _idTrackingDecided = true;
        _idTrackingAvailable = available;

        if (!available)
        {
            // 没有实例 ID 时，用当前已见数量重建
            _maxConcurrentLocal.Clear();
        }
    }

    /// <summary>同帧可见数量兜底：副本复用 CardID 时至少按同时可见数量计入。</summary>
    private bool UpdateOpponentCounts(Dictionary<string, int> current)
    {
        var changed = false;
        foreach (var (code, count) in current)
        {
            if (count <= _maxConcurrentOpponent.GetValueOrDefault(code)) continue;

            _maxConcurrentOpponent[code] = count;
            if (count > _opponentPlayed.GetValueOrDefault(code))
            {
                _opponentPlayed[code] = count;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 手动标记一张牌「已去除」：对手丢弃/移出牌库时接口没有记录，
    /// 由玩家从右键扣 1 张（只影响本局剩余牌库显示，不写入录制）。
    /// </summary>
    public bool MarkRemoved(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        _manualRemoved[code] = _manualRemoved.GetValueOrDefault(code) + 1;
        Revision++;
        return true;
    }

    /// <summary>撤销一次「已去除」。</summary>
    public bool UndoRemoved(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        var count = _manualRemoved.GetValueOrDefault(code);
        if (count <= 0) return false;

        if (count == 1) _manualRemoved.Remove(code);
        else _manualRemoved[code] = count - 1;

        Revision++;
        return true;
    }

    /// <summary>某张牌本局被手动「已去除」的次数。</summary>
    public int ManualRemovedCount(string code) =>
        string.IsNullOrWhiteSpace(code) ? 0 : _manualRemoved.GetValueOrDefault(code);

    private void ApplyConcurrentLocalFallback(Dictionary<string, int> localCounts)
    {
        foreach (var (code, count) in localCounts)
        {
            _maxConcurrentLocal[code] = Math.Max(_maxConcurrentLocal.GetValueOrDefault(code), count);
        }

        foreach (var (code, total) in _initialDeck)
        {
            var used = Math.Min(total, _maxConcurrentLocal.GetValueOrDefault(code));
            _remaining[code] = total - used;
        }
    }

    /// <summary>礼让 / 对局结束：保留最后一帧数据，仅标记状态。</summary>
    public void MarkLeftGame()
    {
        IsInGame = false;
        Revision++;
    }

    public void MarkChanged() => Revision++;
}
