using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using System.Windows.Threading;
using LoRPoCTracker.Config;
using LoRPoCTracker.Models;
using LoRPoCTracker.Services;

namespace LoRPoCTracker.ViewModels;

public sealed class OverlayViewModel : System.ComponentModel.INotifyPropertyChanged, IDisposable
{
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    // 游戏本地化文本中的标记：[Keyword:XX]文本[/]、{Damage:1} 等
    private static readonly Regex LocTagRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex LocValueRegex = new(@"\{[^{}:]+:([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex LocPlaceholderRegex = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    private static readonly Brush Green = MakeBrush(0x4A, 0xDE, 0x80);
    private static readonly Brush Amber = MakeBrush(0xE5, 0xB4, 0x54);
    private static readonly Brush Red = MakeBrush(0xF8, 0x71, 0x71);
    private static readonly Brush Gray = MakeBrush(0x8A, 0x93, 0xA0);

    private readonly AppConfig _config;
    private readonly CardDatabase _cards;
    private readonly LocalizationDatabase _loc;
    private readonly LorApiClient _api;
    private readonly EncounterRepository _encounters;
    private readonly GameLogWatcher _logWatcher;
    private readonly GameDataIndex _gameData;
    private readonly CardImageCache _images;
    private readonly BossAvatarService _avatars;
    private readonly MatchTracker _tracker = new();
    private readonly DispatcherTimer _timer;

    private bool _busy;
    private bool _inGame;
    private bool _committedThisGame;
    private string _encounterLabel = "";
    private string _encounterCampaign = "";
    private string _encounterMapName = "";
    private string _encounterNodeName = "";
    private int _deckAttempts;
    private int _apiFailures;
    private int _lastRevision = -1;



    public OverlayViewModel(AppConfig config, CardDatabase cards, LocalizationDatabase loc,
        GameDataIndex gameData)
    {
        _config = config;
        _cards = cards;
        _loc = loc;
        _gameData = gameData;
        _api = new LorApiClient(config.ApiPort);
        _encounters = new EncounterRepository();
        _logWatcher = new GameLogWatcher();
        _images = new CardImageCache(config.Locale);
        _avatars = new BossAvatarService(_images, config.GameDir, config.Locale);

        DeckRows = new ObservableCollection<CardRow>();
        OpponentRows = new ObservableCollection<CardRow>();
        SeenRows = new ObservableCollection<CardRow>();
        BossRows = new ObservableCollection<CardRow>();
        MapBossRows = new ObservableCollection<MapBossRow>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(config.PollIntervalMs) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CardRow> DeckRows { get; }
    public ObservableCollection<CardRow> OpponentRows { get; }
    public ObservableCollection<CardRow> SeenRows { get; }
    public ObservableCollection<CardRow> BossRows { get; }

    /// <summary>当前大地图下已录入的所有 Boss 牌库（进入地图时展示）。</summary>
    public ObservableCollection<MapBossRow> MapBossRows { get; }

    public MatchTracker Tracker => _tracker;

    private string _statusText = "启动中…";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    private string _statusDetail = "";
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetField(ref _statusDetail, value);
    }

    private Brush _statusBrush = Gray;
    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetField(ref _statusBrush, value);
    }

    private string _deckTitle = "我的牌库";
    public string DeckTitle
    {
        get => _deckTitle;
        private set => SetField(ref _deckTitle, value);
    }

    private string _opponentTitle = "对手已打出";
    public string OpponentTitle
    {
        get => _opponentTitle;
        private set => SetField(ref _opponentTitle, value);
    }

    private string _seenTitle = "本局已见";
    public string SeenTitle
    {
        get => _seenTitle;
        private set => SetField(ref _seenTitle, value);
    }

    private bool _hasDeck;
    public bool HasDeck
    {
        get => _hasDeck;
        private set => SetField(ref _hasDeck, value);
    }

    private bool _hasOpponentRows;
    public bool HasOpponentRows
    {
        get => _hasOpponentRows;
        private set => SetField(ref _hasOpponentRows, value);
    }

    private bool _hasSeenRows;
    public bool HasSeenRows
    {
        get => _hasSeenRows;
        private set => SetField(ref _hasSeenRows, value);
    }

    private string _emptyHint = "";
    public string EmptyHint
    {
        get => _emptyHint;
        private set => SetField(ref _emptyHint, value);
    }

    private bool _showEmptyHint;
    public bool ShowEmptyHint
    {
        get => _showEmptyHint;
        private set => SetField(ref _showEmptyHint, value);
    }

    private bool _showBossSection;
    public bool ShowBossSection
    {
        get => _showBossSection;
        private set => SetField(ref _showBossSection, value);
    }

    private bool _isDeckExpanded = true;
    public bool IsDeckExpanded
    {
        get => _isDeckExpanded;
        set => SetField(ref _isDeckExpanded, value);
    }

    private bool _isBossExpanded = true;
    public bool IsBossExpanded
    {
        get => _isBossExpanded;
        set => SetField(ref _isBossExpanded, value);
    }

    private bool _isOpponentExpanded = true;
    public bool IsOpponentExpanded
    {
        get => _isOpponentExpanded;
        set => SetField(ref _isOpponentExpanded, value);
    }

    private bool _isSeenExpanded = true;
    public bool IsSeenExpanded
    {
        get => _isSeenExpanded;
        set => SetField(ref _isSeenExpanded, value);
    }

    private string _bossTitle = "Boss 牌库";
    public string BossTitle
    {
        get => _bossTitle;
        private set => SetField(ref _bossTitle, value);
    }

    private string _bossSubtitle = "";
    public string BossSubtitle
    {
        get => _bossSubtitle;
        private set => SetField(ref _bossSubtitle, value);
    }

    private bool _hasBossRows;
    public bool HasBossRows
    {
        get => _hasBossRows;
        private set => SetField(ref _hasBossRows, value);
    }

    private bool _showMapBossSection;
    /// <summary>是否显示「本地图 Boss 牌库」下拉（仅在英雄之路大地图上显示）。</summary>
    public bool ShowMapBossSection
    {
        get => _showMapBossSection;
        private set => SetField(ref _showMapBossSection, value);
    }

    private bool _isMapBossExpanded = true;
    public bool IsMapBossExpanded
    {
        get => _isMapBossExpanded;
        set => SetField(ref _isMapBossExpanded, value);
    }

    private string _mapBossTitle = "本地图 Boss 牌库";
    public string MapBossTitle
    {
        get => _mapBossTitle;
        private set => SetField(ref _mapBossTitle, value);
    }

    private string _mapBossSubtitle = "";
    public string MapBossSubtitle
    {
        get => _mapBossSubtitle;
        private set => SetField(ref _mapBossSubtitle, value);
    }

    private bool _hasMapBossRows;
    public bool HasMapBossRows
    {
        get => _hasMapBossRows;
        private set => SetField(ref _hasMapBossRows, value);
    }

    private string _mapBossEmptyHint = "";
    /// <summary>本局已打过全部 Boss 时的提示。</summary>
    public string MapBossEmptyHint
    {
        get => _mapBossEmptyHint;
        private set
        {
            if (string.Equals(_mapBossEmptyHint, value, StringComparison.Ordinal)) return;
            _mapBossEmptyHint = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MapBossEmptyHint)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MapBossEmptyHintVisibility)));
        }
    }

    public System.Windows.Visibility MapBossEmptyHintVisibility =>
        _mapBossEmptyHint.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public string DeckCode => _tracker.DeckCode ?? "";

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void ForceRefresh() => RefreshRows(force: true);

    /// <summary>供外部（卡牌数据加载、初始化）更新状态区文字。</summary>
    public void SetStatus(string text, string detail, bool isBusy)
    {
        StatusText = text;
        StatusDetail = detail;
        StatusBrush = isBusy ? Amber : Gray;
    }

    /// <summary>手动重置本局记录（仅管理员版；每局开始的自动重置不受影响）。</summary>
    public void ResetMatch()
    {
        if (!_config.AdminMode) return;

        _tracker.Reset();
        _inGame = false;
        _committedThisGame = false;
        _deckAttempts = 0;
        _apiFailures = 0;
        RefreshRows(force: true);
        StatusText = "已重置";
        StatusDetail = "等待下一局对局";
        StatusBrush = Amber;
    }

    /// <summary>当前是否为管理员版（普通版只读使用已录制牌库，不写入任何录制数据）。</summary>
    public bool IsAdminMode => _config.AdminMode;

    /// <summary>能否写入录制数据：必须是管理员版，且当前数据源为本地源（网络源只读）。</summary>
    public bool CanWriteEncounters => _config.AdminMode && _encounters.IsLocalView;

    /// <summary>当前数据源是否为本地源。</summary>
    public bool IsLocalSource => _encounters.IsLocalView;

    /// <summary>当前数据源名称。</summary>
    public string ActiveSourceName => _encounters.ViewSourceName;

    /// <summary>按配置中的当前数据源加载牌库视图；网络源全部失败时自动回退本地源。</summary>
    public async Task<string> ApplyActiveSourceAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var source = _config.Sources.FirstOrDefault(item => item.Id == _config.ActiveSourceId);
        if (source is null || source.IsLocal)
        {
            _encounters.UseLocalView();
            RebindCurrentEncounter();
            ForceRefresh();
            return "当前数据源：本地录制";
        }

        var result = await EncounterSourceLoader.LoadAsync(source, forceRefresh, ct);
        if (result.Records.Count == 0 || result.FailedFiles >= result.FileCount)
        {
            // 仅本次临时回退本地视图，不改写配置：下次启动仍会重试默认云源
            _encounters.UseLocalView();
            RebindCurrentEncounter();
            ForceRefresh();
            return $"「{source.Name}」加载失败（{result.Message}），本次已临时回退本地录制源";
        }

        var merged = EncounterSourceLoader.Merge(result.Records);
        _encounters.UseExternalView(merged, source.Name);
        RebindCurrentEncounter();
        ForceRefresh();
        return $"当前数据源：{source.Name} · {result.Message}";
    }

    /// <summary>切换当前数据源并立即加载。</summary>
    public async Task<string> SetActiveSourceAsync(string id, bool forceRefresh = false)
    {
        if (_config.Sources.All(item => item.Id != id)) return "数据源不存在";

        _config.ActiveSourceId = id;
        _config.Save();
        return await ApplyActiveSourceAsync(forceRefresh);
    }

    /// <summary>切换数据源后，把当前对局重新绑定到新视图中的录制记录。</summary>
    private void RebindCurrentEncounter()
    {
        var key = _tracker.OpponentEncounterKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        _tracker.SetEncounter(key, _encounters.Get(key));
    }

    /// <summary>把本局观察到的 Boss 出牌合并进当前关卡的录制。</summary>
    public void CommitCurrentGame()
    {
        if (_committedThisGame || !CanWriteEncounters || !_config.RecordBossDecks) return;

        var key = _tracker.OpponentEncounterKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        if (_tracker.OpponentPlayed.Count == 0) return;

        // 定档牌库：内容已人工确认，之后到这个关卡都不再自动录制（仍可手动编辑）
        if (_encounters.Get(key)?.IsFinalized == true)
        {
            _committedThisGame = true;
            return;
        }

        IReadOnlyDictionary<string, int> observed = _tracker.OpponentPlayed;
        var filtered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, count) in _tracker.OpponentPlayed)
        {
            // 诅咒是我方塞进对手牌库的卡，算衍生，只显示不写入
            if (IsCurse(code)) continue;

            // 英雄法术 / 武器会反复打出，只按 1 张实体牌录制
            if (IsChampionSpell(code) || IsEquipment(code))
            {
                filtered[code] = 1;
                continue;
            }

            // 其他衍生物只显示，不写入 Boss 牌库
            if (_cards.TryGet(code, out var info) && !info.Collectible) continue;
            filtered[code] = count;
        }

        observed = filtered;

        if (observed.Count == 0) return;

        var record = _encounters.Commit(key, _encounterCampaign, _encounterMapName, _encounterNodeName, observed);
        _tracker.SetEncounter(key, record);
        _committedThisGame = true;
        ForceRefresh();
    }

    /// <summary>清除当前 Boss 的历史录制（仅管理员版）。</summary>
    public void ClearCurrentBossRecord()
    {
        if (!CanWriteEncounters) return;

        var key = _tracker.OpponentEncounterKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        _encounters.Clear(key);
        _tracker.SetEncounter(key, null);
        _committedThisGame = false;
        ForceRefresh();
    }

    public bool HasCurrentEncounter => !string.IsNullOrWhiteSpace(_tracker.OpponentEncounterKey);

    /// <summary>清理历史录制中的衍生物（卡牌数据加载完成后调用；英雄法术与武器保留）。</summary>
    public void PruneGeneratedCards()
    {
        if (!CanWriteEncounters) return;

        var removed = _encounters.Prune(code =>
            IsCurse(code) ||
            (_cards.TryGet(code, out var info) && !info.Collectible && !IsChampionSpell(code)));

        if (removed > 0) ForceRefresh();
    }

    public string? CurrentEncounterKey => _tracker.OpponentEncounterKey;

    public bool IsInGame => _tracker.IsInGame;

    /// <summary>当前对局对手已打出的牌（编辑 Boss 牌库时单向同步；已排除衍生物与手动标记）。</summary>
    public IReadOnlyDictionary<string, int> CurrentOpponentPlayed
    {
        get
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (code, count) in _tracker.OpponentPlayed)
            {
                // 诅咒 / 衍生物只显示，不写入 Boss 牌库（与自动录制保持一致）
                if (IsCurse(code)) continue;
                if (_cards.TryGet(code, out var info) && !info.Collectible) continue;
                if (_tracker.Encounter?.IsToken(code) == true) continue;

                // 武器会回手反复打出，编辑器同步也只按 1 张实体计
                result[code] = IsEquipment(code) ? 1 : count;
            }

            return result;
        }
    }

    /// <summary>指定关卡是否已把某张牌标记为衍生。</summary>
    public bool IsEncounterToken(string key, string code) =>
        _encounters.Get(key)?.IsToken(code) ?? false;

    /// <summary>手动标记一张牌「已去除」：从 Boss 剩余牌库扣 1（不写入录制，仅本局）。</summary>
    public void RemoveBossCard(string code)
    {
        if (!CanWriteEncounters) return;
        if (_tracker.MarkRemoved(code)) ForceRefresh();
    }

    /// <summary>撤销一次手动「已去除」。</summary>
    public void UndoRemoveBossCard(string code)
    {
        if (!CanWriteEncounters) return;
        if (_tracker.UndoRemoved(code)) ForceRefresh();
    }

    /// <summary>某张牌本局被手动「已去除」的次数。</summary>
    public int ManualRemovedCount(string code) => _tracker.ManualRemovedCount(code);

    /// <summary>把当前对局对手已打出的某张牌标记为衍生：不写入 Boss 牌库录制。</summary>
    public void MarkOpponentToken(string code)
    {
        if (!CanWriteEncounters) return;
        if (string.IsNullOrWhiteSpace(code)) return;

        var key = _tracker.OpponentEncounterKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        var record = _encounters.Ensure(key, _encounterCampaign, _encounterMapName, _encounterNodeName);
        _tracker.SetEncounter(key, record);
        _encounters.SetToken(key, code, true);
        ForceRefresh();
    }

    public string CurrentEncounterDisplayName => _tracker.Encounter is not null
        ? DisplayNameOf(_tracker.Encounter)
        : JoinName(_encounterCampaign, _encounterNodeName);

    /// <summary>对局中手动编辑当前 Boss 牌库时，确保记录存在。</summary>
    public EncounterRecord? EnsureCurrentEncounter()
    {
        if (!CanWriteEncounters) return null;

        var key = _tracker.OpponentEncounterKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        var record = _encounters.Ensure(key, _encounterCampaign, _encounterMapName, _encounterNodeName);
        _tracker.SetEncounter(key, record);
        return record;
    }

    public void SetEncounterCard(string key, string code, int count)
    {
        if (!CanWriteEncounters) return;
        if (string.IsNullOrWhiteSpace(key)) return;

        // 诅咒 / 衍生物不能写进牌库；count=0 表示删除，仍允许清掉历史误录
        if (count > 0 && IsGeneratedCard(code)) return;

        _encounters.SetCard(key, code, count);
        ForceRefresh();
    }

    /// <summary>手动设置关卡的预定牌库总张数（0 表示不对比）。</summary>
    public void SetEncounterExpectedCount(string key, int count)
    {
        if (!CanWriteEncounters) return;
        if (string.IsNullOrWhiteSpace(key)) return;

        _encounters.SetExpectedCount(key, count);
        ForceRefresh();
    }

    /// <summary>指定关卡是否已定档。</summary>
    public bool IsEncounterFinalized(string key) => _encounters.Get(key)?.IsFinalized == true;

    /// <summary>定档 / 取消定档牌库（仅管理员版）；定档后该关卡不再自动录制，但仍可编辑。</summary>
    public void SetEncounterFinalized(string key, bool finalized)
    {
        if (!CanWriteEncounters) return;
        if (string.IsNullOrWhiteSpace(key)) return;

        _encounters.SetFinalized(key, finalized);
        ForceRefresh();
    }

    /// <summary>按 key 获取任意关卡的录制（录制管理中编辑牌库用）。</summary>
    public EncounterRecord? GetEncounter(string key) => _encounters.Get(key);

    /// <summary>任意录制的显示名（不借用当前对局信息）。</summary>
    public string GetEncounterDisplayName(string key)
    {
        var record = _encounters.Get(key);
        if (record is null) return key;

        var text = JoinName(record.Campaign, record.MapName, record.NodeName);
        return string.IsNullOrWhiteSpace(text) ? record.Key : text;
    }

    /// <summary>
    /// 搜索卡牌：Data Dragon 结果 + 游戏本地化兜底，
    /// 让活动/PvE 等 Data Dragon 未收录的新卡（如 99PZ128 震動）也能搜到。
    /// </summary>
    public IReadOnlyList<CardInfo> SearchCards(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<CardInfo>();

        var text = query.Trim();
        var results = _cards.Search(text).ToList();

        foreach (var (code, name) in _loc.GetCardNames())
        {
            if (_cards.TryGet(code, out _)) continue;
            if (!name.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !code.StartsWith(text, StringComparison.OrdinalIgnoreCase)) continue;

            var fallback = CardDatabase.TryGetFallback(code, out var extra) ? extra : null;
            results.Add(new CardInfo
            {
                Code = code,
                Name = name,
                Cost = fallback?.Cost ?? -1,
                Type = fallback?.Type ?? "",
                Rarity = fallback?.Rarity ?? "",
                RegionRef = fallback?.RegionRef ?? ""
            });
        }

        return results
            .OrderBy(card => card.Name, StringComparer.CurrentCulture)
            .Take(40)
            .ToList();
    }

    /// <summary>是否开启卡牌图片展示。</summary>
    public bool ShowCardImages => _config.ShowCardImages;

    /// <summary>已加载/已缓存的内存图片（同步）。</summary>
    public ImageSource? TryGetCachedCardImage(string code) =>
        ShowCardImages ? _images.TryGetMemory(code) : null;

    /// <summary>读取本地卡牌图片；本地没有时按 Data Dragon 地址下载一次。</summary>
    public Task<ImageSource?> LoadCardImageAsync(string code)
    {
        if (!ShowCardImages) return Task.FromResult<ImageSource?>(null);
        return _images.LoadAsync(code, FindCardInfo(code)?.ImageUrl);
    }

    /// <summary>
    /// 该卡是否有官方卡图（标准卡 / PvE 资源包 / dd_code 复用）。
    /// false 表示界面上显示的是本地拼合卡面（官方从未发布这张卡的图）。
    /// </summary>
    public bool HasOfficialImage(string code)
    {
        var info = FindCardInfo(code);
        return !string.IsNullOrWhiteSpace(info?.ImageUrl) &&
               info.ImageUrl.Contains("dd.b.pvp.net", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>诅咒卡：玩家塞进对手牌库的法术，一律按衍生处理，不写入 Boss 牌库。</summary>
    private bool IsCurse(string code)
    {
        var info = FindCardInfo(code);
        if (info is null) return false;

        if (info.KeywordRefs.Any(key => key.Equals("Curse", StringComparison.OrdinalIgnoreCase)) ||
            info.SubtypeRefs.Any(key => key.Equals("Curse", StringComparison.OrdinalIgnoreCase)))
            return true;

        return HasCurseText(info.Type) ||
               info.Keywords.Any(key => HasCurseText(key)) ||
               info.Subtypes.Any(key => HasCurseText(key));
    }

    private static bool HasCurseText(string? text) =>
        text is not null &&
        (text.Contains("詛咒") || text.Contains("诅咒") ||
         text.Contains("Curse", StringComparison.OrdinalIgnoreCase));

    /// <summary>武器 / 装备：装备单位阵亡后回手，可反复打出，牌库只按 1 张实体计。</summary>
    private bool IsEquipment(string code)
    {
        var info = FindCardInfo(code);
        if (info is null) return false;

        if (info.KeywordRefs.Any(key => key.Equals("Equipment", StringComparison.OrdinalIgnoreCase)) ||
            info.SubtypeRefs.Any(key => key.Equals("Equipment", StringComparison.OrdinalIgnoreCase)))
            return true;

        return info.Type.Contains("裝備") ||
               info.Type.Contains("装备") ||
               info.Type.Contains("Equipment", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>不应写入 Boss 牌库的衍生卡（诅咒 + 数据标记为不可收集的衍生物）。</summary>
    public bool IsGeneratedCard(string code)
    {
        if (IsCurse(code)) return true;
        return FindCardInfo(code)?.Collectible == false && !IsChampionSpell(code);
    }

    /// <summary>
    /// 是否为英雄法术：英雄在手牌中转化出的法术（非可收集，SuperType 为英雄）。
    /// 这类牌打出只显示在「已打出」，不扣 Boss 牌库剩余；录制只记 1 张。
    /// </summary>
    private bool IsChampionSpell(string code)
    {
        // 诅咒是塞进对手牌库的衍生物，即使由英雄牌派生也不能按英雄法术录制
        if (IsCurse(code)) return false;

        var info = FindCardInfo(code);
        if (info is null) return false;

        var isSpell = info.Type.Contains("法術") ||
                      info.Type.Contains("法术") ||
                      info.Type.Contains("Spell", StringComparison.OrdinalIgnoreCase);
        if (!isSpell) return false;

        if (info.SuperType.Contains("英雄") ||
            info.SuperType.Contains("Champion", StringComparison.OrdinalIgnoreCase) ||
            info.Rarity.Contains("英雄") ||
            info.Rarity.Contains("Champion", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 兜底：去掉 T1/T2 后缀后能对到英雄牌
        var match = Regex.Match(code, @"^(.+?)T\d+$", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        var baseInfo = FindCardInfo(match.Groups[1].Value);
        return baseInfo is not null &&
               (baseInfo.SuperType.Contains("英雄") ||
                baseInfo.SuperType.Contains("Champion", StringComparison.OrdinalIgnoreCase) ||
                baseInfo.Rarity.Contains("英雄") ||
                baseInfo.Rarity.Contains("Champion", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>重新生成本地图片后刷新界面：清空内存缓存并重新载入可见行。</summary>
    public async Task ReloadImagesAsync()
    {
        _images.ClearMemory();
        _avatars.ClearMemory();

        var rows = DeckRows.Concat(BossRows).Concat(OpponentRows).Concat(SeenRows)
            .GroupBy(row => row.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        foreach (var row in rows)
        {
            var image = await LoadCardImageAsync(row.Code);
            row.ImageSource = image;
            row.ImageRequested = image is not null;
        }

        // 地图 Boss 头像重新生成（卡牌资源包更新后可能补上了插画）
        var bossCodes = MapBossRows
            .Select(row => row.BossCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (bossCodes.Count > 0) await LoadMapBossAvatarsAsync(bossCodes);
    }

    public CardEditRow CreateEditRow(string code, int count)
    {
        var info = FindCardInfo(code);
        var name = info?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = _loc.GetCardName(code);
        if (string.IsNullOrWhiteSpace(name)) name = code;

        return new CardEditRow
        {
            Code = code,
            Name = name,
            Cost = info?.Cost ?? -1,
            Count = count,
            RegionBrush = RegionPalette.Get(info?.RegionRef ?? ""),
            IsToken = IsGeneratedCard(code)
        };
    }

    /// <summary>卡牌数据：优先 Data Dragon；游戏独占卡用内置补充数据，
    /// 已知有 Data Dragon 对应卡的（dd_code）直接复用网络卡数据/卡图。</summary>
    private CardInfo? FindCardInfo(string code)
    {
        if (_cards.TryGet(code, out var info) && info is not null) return info;
        if (!CardDatabase.TryGetFallback(code, out var fallback) || fallback is null) return null;

        if (!string.IsNullOrWhiteSpace(fallback.DataDragonCode) &&
            _cards.TryGet(fallback.DataDragonCode, out var network) && network is not null)
        {
            return network;
        }

        return fallback;
    }

    private List<(string Squashed, string Path)>? _artIndex;

    /// <summary>清空原画索引（首次生成 99 系列原画后调用，避免索引缺新文件）。</summary>
    private void ResetArtIndex() => _artIndex = null;

    /// <summary>本地原画文件路径（local_assets/illustrations，支持文件名包含卡牌代码）。</summary>
    private string? FindArtPath(string code)
    {
        var dir = Path.Combine(AppPaths.DataDir, "local_assets", "illustrations");
        var direct = Path.Combine(dir, code + ".png");
        if (File.Exists(direct)) return direct;
        if (!Directory.Exists(dir)) return null;

        if (_artIndex is null)
        {
            _artIndex = Directory.EnumerateFiles(dir, "*.png")
                .Select(path => (Squash(Path.GetFileNameWithoutExtension(path)), path))
                .ToList();
        }

        var target = Squash(code);
        foreach (var (name, path) in _artIndex)
        {
            if (name.Contains(target, StringComparison.Ordinal)) return path;
        }

        return null;
    }

    private static string Squash(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 用本地资源拼一张完整卡图（.NET 内渲染），返回是否成功。
    /// 在线图库收录的卡默认不拼，直接用官方卡图；只拼未收录（无官方图）的卡。
    /// </summary>
    public bool TryComposeCard(string code, string outputDir, out string outputPath,
        string? fallbackArt = null)
    {
        outputPath = "";
        var info = FindCardInfo(code);

        // 官方有卡图的卡不拼：由资源包落地或 CardImageCache 按需下载，
        // 占位卡面不能挡住官方图；只有 DD 未收录的卡才用占位原画拼图。
        if (CardDatabase.IsDataDragonDuplicate(code)) return false;
        if (!string.IsNullOrWhiteSpace(info?.ImageUrl) &&
            info.ImageUrl.Contains("dd.b.pvp.net", StringComparison.OrdinalIgnoreCase)) return false;

        var art = FindArtPath(code) ?? fallbackArt;
        if (art is null) return false;

        var name = info?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = _loc.GetCardName(code);
        if (string.IsNullOrWhiteSpace(name)) name = code;

        var description = CleanLocText(_loc.Get($"card_{code}_Description"));
        if (string.IsNullOrWhiteSpace(description)) description = info?.Description ?? "";
        var kind = CardImageComposer.PickKind(
            (info?.SuperType ?? "") + (info?.Type ?? ""), info?.SpellSpeed);
        var iconName = (info?.SpellSpeed ?? "").Contains("陷阱")
            ? "icon_trap_crispmip.png"
            : (info?.SpellSpeed ?? "").Contains("專注") || (info?.SpellSpeed ?? "").Contains("专注")
                ? "icon_focus_crispmip.png"
                : kind switch
                {
                    "spell_slow" => "icon_slow_crispmip.png",
                    "spell_fast" => "icon_fast_crispmip.png",
                    "spell_burst" => "icon_burst_crispmip.png",
                    "spell_action" => "icon_skill_crispmip.png",
                    _ => ""
                };
        var icon = CardImageComposer.ResolveSpeedIcon(iconName);

        // 顶部特性条：显示种族/特性（如「魄羅」「約德爾」），没有则不显示
        var topLabel = info is null ? "" : string.Join(" ", info.Subtypes);

        // 名称下方关键词行：图标 + 文字（关键词英文标识用于匹配图标）
        var keywords = new List<CardKeyword>();
        if (info is not null)
        {
            for (var i = 0; i < info.Keywords.Count; i++)
            {
                var keywordRef = i < info.KeywordRefs.Count ? info.KeywordRefs[i] : "";
                keywords.Add(new CardKeyword(info.Keywords[i], keywordRef));
            }
        }

        var regionRefs = info?.RegionRefs is { Count: > 0 } list
            ? list
            : (string.IsNullOrWhiteSpace(info?.RegionRef) ? new List<string>() : new List<string> { info!.RegionRef });

        // 99 系列按代码前缀补区域（如 99DE/99NX/99NU），避免缺区域图标
        if (regionRefs.Count == 0 && RegionPalette.RegionRefFromCode(code) is { } derivedRegion)
        {
            regionRefs = new List<string> { derivedRegion };
        }

        var data = new CardComposeData(
            code,
            name,
            info?.Cost,
            info?.Attack,
            info?.Health,
            kind,
            topLabel,
            regionRefs,
            keywords,
            description,
            info?.LevelUpDescription ?? "",
            art,
            icon,
            info?.SpellSpeed ?? "");

        Directory.CreateDirectory(outputDir);
        outputPath = Path.Combine(outputDir, code + ".png");
        CardImageComposer.Compose(data, outputPath);
        return true;
    }

    /// <summary>
    /// 资源包导入后的收尾：只补拼「尚无本地图」的游戏独占卡。
    /// 不删除任何已有图（包括刚从 PvE 资源包导入的官方卡图）。
    /// </summary>
    public async Task<int> ComposeMissingCardsAsync(IProgress<string>? progress = null)
    {
        var outputDir = Path.Combine(AppPaths.DataDir, "images", _config.Locale);
        Directory.CreateDirectory(outputDir);

        var codes = GetComposableCodes();

        var placeholder = CardImageComposer.EnsurePlaceholderArt();
        var composed = 0;
        var busy = 0;
        foreach (var code in codes)
        {
            if (File.Exists(Path.Combine(outputDir, code + ".png"))) continue;
            if (TryComposeCard(code, outputDir, out _, placeholder))
            {
                composed++;
                if (composed % 50 == 0) progress?.Report($"已补拼 {composed} 张游戏独占卡");
            }

            if (++busy % 25 == 0) await Task.Yield();
        }

        _images.ClearMemory();
        return composed;
    }

    /// <summary>全部 99 系列卡牌代码（含无本地化名称的变体）。</summary>
    public IReadOnlyList<string> Get99CardCodes() => _cards.AllCodes
        .Concat(CardDatabase.ExtraCardCodes)
        .Concat(_loc.GetCardNames().Select(item => item.Code))
        .Where(code => code.StartsWith("99", StringComparison.OrdinalIgnoreCase) &&
                       Regex.IsMatch(code, @"^\d{2}[A-Za-z]{2}\d{3}"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// 99 系列专用流程：首次启动时从游戏资源生成卡框元素/原画/取景参数/阵营图标，
    /// 然后重新拼合全部 99 系列卡面（不依赖预生成资源）。
    /// </summary>
    public async Task<(GameAssetResult Result, int Composed)> Ensure99CardAssetsAsync(
        IProgress<string>? progress = null, bool force = false)
    {
        var codes = Get99CardCodes();
        var extractor = new GameAssetExtractor(_config.GameDir, _config.Locale);
        var result = await Task.Run(() => extractor.EnsureAsync(codes, progress, force)).ConfigureAwait(true);

        CardImageComposer.ResetRoiCache();
        ResetArtIndex();
        _images.ClearMemory();

        var outputDir = Path.Combine(AppPaths.DataDir, "images", _config.Locale);
        Directory.CreateDirectory(outputDir);

        // 本次真的从游戏生成了资源：删掉旧拼图，按新素材重拼
        if (result.Extracted)
        {
            var deleted = 0;
            foreach (var code in codes)
            {
                try
                {
                    var path = Path.Combine(outputDir, code + ".png");
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch
                {
                    // 图片被占用时保留旧图
                }
            }

            var left = Directory.EnumerateFiles(outputDir, "99*.png").Count();
            progress?.Report($"已清理旧 99 卡图 {deleted} 张，目录剩余 {left} 张");
        }

        var placeholder = CardImageComposer.EnsurePlaceholderArt();
        var composed = 0;
        var existing = 0;
        var failed = 0;
        var busy = 0;
        foreach (var code in codes)
        {
            if (File.Exists(Path.Combine(outputDir, code + ".png")))
            {
                existing++;
                continue;
            }

            if (TryComposeCard(code, outputDir, out _, placeholder))
            {
                composed++;
                if (composed % 20 == 0) progress?.Report($"正在拼合 99 系列卡图 {composed}/{codes.Count}");
            }
            else
            {
                failed++;
            }

            if (++busy % 10 == 0) await Task.Yield();
        }

        _images.ClearMemory();
        progress?.Report($"99 系列卡图：新拼 {composed} 张 / 已有 {existing} 张 / 跳过 {failed} 张");
        return (result, composed);
    }

    private List<string> GetComposableCodes() => _cards.AllCodes
        .Concat(CardDatabase.ExtraCardCodes)
        .Concat(_loc.GetCardNames().Select(item => item.Code))
        .Where(code => Regex.IsMatch(code, @"^\d{2}[A-Za-z]{2}\d{3}"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
        .ToList();

    // ---------------------------------------------------------------- 本地图 Boss 牌库

    private string? _mapBossAdventureKey;
    private string _mapBossSignature = "";

    /// <summary>当前大地图对应的本地化 key（如 SpiritBlossom_Adventure_Bilgewater_Name）。</summary>
    private string? ResolveAdventureKey()
    {
        var key = _gameData.GetCampaignKey(_logWatcher.AdventureGuid);
        return LocalizationDatabase.IsMapKey(key) ? key : null;
    }

    /// <summary>
    /// 进入英雄之路大地图后，把该地图已录入的 Boss 牌库汇总成一个下拉分区；
    /// 离开地图（进战斗 / 回主菜单）时隐藏。
    /// </summary>
    private void UpdateMapBosses()
    {
        var adventureKey = _logWatcher.IsInAdventureMap ? ResolveAdventureKey() : null;
        if (adventureKey is null || !_config.ShowMapBossDecks)
        {
            HideMapBossSection();
            return;
        }

        var campaign = _loc.GetCampaignName(adventureKey) ?? "";
        var mapName = _loc.GetMapName(adventureKey) ?? "";
        var all = GetMapBossRecords(adventureKey, campaign, mapName);

        if (all.Count == 0)
        {
            HideMapBossSection();
            _mapBossAdventureKey = adventureKey;
            return;
        }

        // 本局已经打过的 Boss：日志里本 run 出现过的战斗对手
        var runBosses = new HashSet<string>(_logWatcher.GetRunBosses(), StringComparer.OrdinalIgnoreCase);
        var visible = _config.HideDefeatedMapBosses
            ? all.Where(record => !runBosses.Contains(record.BossKey)).ToList()
            : all;

        var signature = string.Join("#",
            BuildMapBossSignature(all),
            string.Join(",", runBosses.OrderBy(item => item, StringComparer.Ordinal)),
            _config.HideDefeatedMapBosses ? "1" : "0");

        if (!string.Equals(adventureKey, _mapBossAdventureKey, StringComparison.Ordinal) ||
            !string.Equals(signature, _mapBossSignature, StringComparison.Ordinal))
        {
            _mapBossAdventureKey = adventureKey;
            _mapBossSignature = signature;
            BuildMapBossRows(visible, runBosses);
        }

        var excluded = all.Count - visible.Count;
        ShowMapBossSection = true;
        HasMapBossRows = MapBossRows.Count > 0;
        MapBossTitle = excluded > 0
            ? $"本地图 Boss 牌库   {MapBossRows.Count}（已打过 {excluded}）"
            : $"本地图 Boss 牌库   {MapBossRows.Count}";
        MapBossSubtitle = JoinName(campaign, mapName);

        MapBossEmptyHint = MapBossRows.Count == 0
            ? $"本局已打过全部 {all.Count} 个 Boss（可在菜单打开「显示本局已打过的 Boss」）"
            : "";
    }

    private void HideMapBossSection()
    {
        if (ShowMapBossSection) ShowMapBossSection = false;
        if (MapBossRows.Count > 0) MapBossRows.Clear();
        HasMapBossRows = false;
        _mapBossSignature = "";
        if (MapBossEmptyHint.Length > 0) MapBossEmptyHint = "";
    }

    /// <summary>
    /// 本地图已录入的 Boss：优先按 adventureKey 前缀匹配；
    /// 旧版本录制的键只有 card_xxx_Name，用「战役 + 地图名」归类。
    /// </summary>
    private List<EncounterRecord> GetMapBossRecords(string adventureKey, string campaign, string mapName)
    {
        var prefix = adventureKey + "|";
        return _encounters.All.Values
            .Where(record => !string.IsNullOrWhiteSpace(record.Key))
            .Where(record => record.Cards.Count > 0)
            .Where(record => record.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                             IsLegacyRecordOfMap(record, campaign, mapName))
            .OrderByDescending(record => record.LastSeen)
            .ThenBy(record => record.NodeName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static bool IsLegacyRecordOfMap(EncounterRecord record, string campaign, string mapName)
    {
        if (record.Key.Contains('|')) return false;
        if (string.IsNullOrWhiteSpace(mapName)) return false;
        if (!string.Equals(record.MapName?.Trim(), mapName.Trim(), StringComparison.Ordinal)) return false;
        if (string.IsNullOrWhiteSpace(record.Campaign)) return true;
        if (string.IsNullOrWhiteSpace(campaign)) return false;
        return string.Equals(record.Campaign.Trim(), campaign.Trim(), StringComparison.Ordinal);
    }

    private static string BuildMapBossSignature(List<EncounterRecord> records)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var record in records)
        {
            builder.Append(record.Key).Append('|')
                .Append(record.IsFinalized ? '1' : '0').Append('|')
                .Append(record.NodeName).Append('|');

            foreach (var (code, count) in record.Cards.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                builder.Append(code).Append('=').Append(count).Append(',');
            }

            builder.Append(';');
        }

        return builder.ToString();
    }

    private void BuildMapBossRows(List<EncounterRecord> records, HashSet<string> runBosses)
    {
        var previous = MapBossRows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var rows = new List<MapBossRow>(records.Count);
        var missingAvatars = new List<string>();

        foreach (var record in records)
        {
            var row = CreateMapBossRow(record, runBosses);

            // 重建时保留展开状态与已加载的头像，避免列表闪烁
            if (previous.TryGetValue(record.Key, out var old))
            {
                row.IsExpanded = old.IsExpanded;
                if (old.Avatar is not null) row.Avatar = old.Avatar;
            }

            FillMapBossCards(row, record);
            rows.Add(row);

            if (row.Avatar is null && !string.IsNullOrWhiteSpace(row.BossCode)) missingAvatars.Add(row.BossCode);
        }

        MapBossRows.Clear();
        foreach (var row in rows) MapBossRows.Add(row);

        if (missingAvatars.Count > 0) _ = PrewarmMapBossAvatarsAsync(missingAvatars);
    }

    private MapBossRow CreateMapBossRow(EncounterRecord record, HashSet<string> runBosses)
    {
        var code = EncounterKeys.FromOpponentName(record.BossKey) ?? "";
        var info = code.Length > 0 ? FindCardInfo(code) : null;

        var name = record.NodeName;
        if (string.IsNullOrWhiteSpace(name)) name = info?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = _loc.GetCardName(code);
        if (string.IsNullOrWhiteSpace(name)) name = record.BossKey;

        var total = record.Cards.Values.Sum();
        var isCurrent = string.Equals(record.Key, _tracker.OpponentEncounterKey, StringComparison.Ordinal);
        var isCleared = runBosses.Contains(record.BossKey);

        var detail = $"{record.Cards.Count} 种";

        var badge = isCurrent
            ? "本局"
            : isCleared
                ? "已打过"
                : record.IsFinalized
                    ? "已定档"
                    : "";

        return new MapBossRow
        {
            Key = record.Key,
            BossCode = code,
            Name = name,
            Detail = detail,
            CountText = $"{total} 张",
            Badge = badge,
            IsFinalized = record.IsFinalized,
            AvatarBrush = RegionPalette.Get(info?.RegionRef ?? ""),
            Initial = BuildInitial(name),
            Avatar = code.Length > 0 ? _avatars.TryGetMemory(code) : null
        };
    }

    private static string BuildInitial(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var trimmed = name.Trim();
        var first = char.ToUpperInvariant(trimmed[0]);
        return first.ToString();
    }

    private void FillMapBossCards(MapBossRow row, EncounterRecord record)
    {
        row.Cards.Clear();
        foreach (var item in record.Cards
                     .Select(pair => CreateRow(pair.Key, pair.Value, pair.Value))
                     .Where(item => item is not null)
                     .Select(item => item!)
                     .OrderBy(item => item.Cost < 0 ? 99 : item.Cost)
                     .ThenBy(item => item.Name, StringComparer.CurrentCulture))
        {
            row.Cards.Add(item);
        }
    }

    private async Task PrewarmMapBossAvatarsAsync(List<string> codes)
    {
        try
        {
            // 一次性把缺失插画从游戏资源生成出来，再填充头像
            await _avatars.PrewarmAsync(codes);
            await LoadMapBossAvatarsAsync(codes);
        }
        catch
        {
            // 头像加载失败不影响列表
        }
    }

    private async Task LoadMapBossAvatarsAsync(IEnumerable<string> codes)
    {
        foreach (var code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var image = await _avatars.LoadAsync(code, FindCardInfo(code)?.ImageUrl);
            if (image is null) continue;

            foreach (var row in MapBossRows.Where(item =>
                         string.Equals(item.BossCode, code, StringComparison.OrdinalIgnoreCase)))
            {
                row.Avatar = image;
            }
        }
    }

    /// <summary>按卡牌代码读取 Boss 头像（编辑 Boss 牌库窗口用）。</summary>
    public async Task<ImageSource?> LoadBossAvatarAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var cached = _avatars.TryGetMemory(code);
        if (cached is not null) return cached;

        return await _avatars.LoadAsync(code, FindCardInfo(code)?.ImageUrl);
    }

    /// <summary>全部关卡录制（最近更新优先），供管理窗口使用。</summary>
    public IReadOnlyList<EncounterRecord> GetAllEncounters()
    {
        return _encounters.All.Values
            .OrderByDescending(record => record.LastSeen)
            .ToList();
    }

    public void DeleteEncounter(string key)
    {
        if (!CanWriteEncounters) return;

        _encounters.Clear(key);
        if (string.Equals(key, _tracker.OpponentEncounterKey, StringComparison.Ordinal))
        {
            _tracker.SetEncounter(key, null);
        }

        ForceRefresh();
    }

    // ---------------------------------------------------------------- 单 Boss 牌库导出 / 导入 / 云源合并

    /// <summary>可合并到本地的网络源（已配置文件地址）。</summary>
    public IReadOnlyList<DataSourceConfig> GetNetworkSources() =>
        _config.Sources.Where(source => !source.IsLocal && source.HasFiles).ToList();

    public string ActiveSourceId => _config.ActiveSourceId;

    /// <summary>本地录制列表（不受当前只读网络视图影响），供导出 / 导入窗口使用。</summary>
    public IReadOnlyList<EncounterRecord> GetLocalEncounters() =>
        _encounters.AllLocal.Values
            .Where(record => !string.IsNullOrWhiteSpace(record.Key))
            .OrderByDescending(record => record.LastSeen)
            .ThenBy(record => record.Campaign, StringComparer.CurrentCulture)
            .ThenBy(record => record.MapName, StringComparer.CurrentCulture)
            .ThenBy(record => record.NodeName, StringComparer.CurrentCulture)
            .ToList();

    public EncounterRecord? GetLocalEncounter(string key) => _encounters.GetLocal(key);

    /// <summary>按录制内容生成显示名（不借用当前对局信息）。</summary>
    public string DescribeDeck(EncounterRecord record)
    {
        var text = JoinName(record.Campaign, record.MapName, record.NodeName);
        if (!string.IsNullOrWhiteSpace(text)) return text;

        var code = EncounterKeys.FromOpponentName(record.BossKey);
        if (!string.IsNullOrWhiteSpace(code))
        {
            var name = _loc.GetCardName(code);
            if (string.IsNullOrWhiteSpace(name) && _cards.TryGet(code, out var info) && info is not null)
                name = info.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        return record.Key;
    }

    /// <summary>
    /// 导出单个 Boss 牌库：仅录制模式（管理员版）且该牌库已归档（定档）时可用。
    /// 成功返回 null，失败返回原因。
    /// </summary>
    public string? ExportDeck(string key, string path)
    {
        if (!_config.AdminMode) return "导出只在录制模式（管理员版）可用。";

        var record = _encounters.GetLocal(key);
        if (record is null) return "本地没有该 Boss 的录制。";
        if (!record.IsFinalized) return "该 Boss 牌库还没有归档（定档），归档后才能导出。";
        if (record.Cards.Count == 0) return "该 Boss 牌库没有任何卡牌，无法导出。";

        try
        {
            EncounterTransfer.Export(record, path);
            return null;
        }
        catch (Exception ex)
        {
            return $"导出失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导入单个 Boss 牌库（普通版 / 录制模式都可用），写入本地录制；
    /// targetKey 非空时覆盖到指定关卡，否则使用文件里的关卡键。
    /// </summary>
    public string ImportDeck(EncounterRecord deck, string? targetKey = null)
    {
        var key = string.IsNullOrWhiteSpace(targetKey) ? deck.Key : targetKey.Trim();
        var incoming = EncounterTransfer.Retarget(deck, key);
        if (incoming.Cards.Count == 0) return "文件里的牌库没有任何卡牌，未导入。";

        var existed = _encounters.GetLocal(key) is not null;
        _encounters.WriteLocalDecks(new[] { incoming });
        RefreshAfterLocalWrite();

        var stored = _encounters.GetLocal(key) ?? incoming;
        var total = stored.Cards.Values.Sum();
        var state = stored.IsFinalized ? "已归档" : "未归档";
        return $"{(existed ? "已覆盖" : "已新增")}本地「{DescribeDeck(stored)}」：" +
               $"{stored.Cards.Count} 种 / {total} 张（{state}）";
    }

    /// <summary>
    /// 把网络源整体合并到本地：只写云端已归档、本地未归档（或本地没有）的牌库，
    /// 本地已归档的牌库保持原样。
    /// </summary>
    public async Task<string> MergeSourceIntoLocalAsync(string sourceId, CancellationToken ct = default)
    {
        var source = _config.Sources.FirstOrDefault(item => item.Id == sourceId);
        if (source is null) return "数据源不存在。";
        if (source.IsLocal) return "本地源就是写入目标，不需要合并。";

        var result = await EncounterSourceLoader.LoadAsync(source, forceRefresh: true, ct);
        if (result.Records.Count == 0)
            return $"「{source.Name}」没有加载到数据（{result.Message}），本地未做任何改动。";

        var cloud = EncounterSourceLoader.Merge(result.Records);
        var plan = EncounterTransfer.PlanSourceMerge(cloud, _encounters.AllLocal);
        var written = _encounters.WriteLocalDecks(plan.Writes);
        if (written > 0) RefreshAfterLocalWrite();

        return $"「{source.Name}」{result.Message}\n{plan.Message}" +
               (written > 0 ? $"\n已写入本地 {written} 个牌库。" : "\n本地没有需要写入的牌库。");
    }

    /// <summary>写入本地后：本地视图下重新绑定当前对局，并刷新界面。</summary>
    private void RefreshAfterLocalWrite()
    {
        if (_encounters.IsLocalView) RebindCurrentEncounter();
        ForceRefresh();
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;

        try
        {
            var snapshot = await _api.GetSnapshotAsync();
            if (snapshot is null)
            {
                _apiFailures++;
                if (_apiFailures >= 2)
                {
                    _inGame = false;
                    StatusText = "未连接游戏";
                    StatusDetail = $"未检测到 127.0.0.1:{_config.ApiPort}，请启动 LoR 并开启第三方工具接口";
                    StatusBrush = Red;
                }

                return;
            }

            _apiFailures = 0;

            if (snapshot.IsInGame)
            {
                if (!_inGame)
                {
                    _inGame = true;
                    _tracker.Reset();
                    _deckAttempts = 0;
                    _committedThisGame = false;
                }

                _tracker.SetNames(snapshot.PlayerName, snapshot.OpponentName);
                BindEncounter(snapshot.OpponentName);

                if (!_tracker.HasDeck && _deckAttempts < 30)
                {
                    _deckAttempts++;
                    var deck = await _api.GetStaticDeckAsync();
                    if (deck is { HasDeck: true })
                    {
                        _tracker.ApplyStaticDeck(deck);
                    }
                }

                _tracker.ApplySnapshot(snapshot);

                if (_tracker.HasDeck)
                {
                    StatusText = "对局中";
                    StatusDetail = BuildMatchDetail(snapshot);
                    StatusBrush = Green;
                }
                else
                {
                    StatusText = "对局中 · 暂无牌库数据";
                    StatusDetail = _deckAttempts >= 30
                        ? "当前模式未返回牌库列表，仅记录已见卡牌"
                        : "正在读取牌库…";
                    StatusBrush = Amber;
                }
            }
            else
            {
                if (_inGame)
                {
                    CommitCurrentGame();
                    _inGame = false;
                    _tracker.MarkLeftGame();
                }

                StatusText = "等待对局";
                StatusDetail = "进入英雄之路战斗后自动记录";
                StatusBrush = Gray;
            }

            UpdateMapBosses();
            RefreshRows();
        }
        catch (Exception ex)
        {
            StatusText = "运行异常";
            StatusDetail = ex.Message;
            StatusBrush = Red;
        }
        finally
        {
            _busy = false;
        }
    }

    private string BuildMatchDetail(GameSnapshot snapshot)
    {
        var player = DisplayName(snapshot.PlayerName, "我方");
        var opponent = _tracker.Encounter is not null
            ? DisplayNameOf(_tracker.Encounter)
            : _loc.Get(_logWatcher.EnemyNameKey) ?? DisplayName(snapshot.OpponentName, "对手");
        return $"{player}  vs  {opponent}";
    }

    /// <summary>「战役 · 大地图 · 节点名」。</summary>
    private string DisplayNameOf(EncounterRecord record)
    {
        var campaign = string.IsNullOrWhiteSpace(record.Campaign) ? _encounterCampaign : record.Campaign;
        var map = string.IsNullOrWhiteSpace(record.MapName) ? _encounterMapName : record.MapName;
        var node = string.IsNullOrWhiteSpace(record.NodeName) ? _encounterNodeName : record.NodeName;
        var text = JoinName(campaign, map, node);
        return string.IsNullOrWhiteSpace(text) ? record.Key : text;
    }

    public IReadOnlyList<string> GetCampaignOptions() => _loc.GetCampaignNames();

    /// <summary>战役 → 该战役拥有的大地图（用于录制管理联动下拉）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetMapOptionsByCampaign() =>
        _loc.GetMapsByCampaign();

    /// <summary>手动设置某关卡的战役名与地图名。</summary>
    public void SetEncounterInfo(string key, string campaign, string mapName)
    {
        if (!CanWriteEncounters) return;

        _encounters.SetNames(key, campaign, mapName);
        ForceRefresh();
    }

    private static string JoinName(params string?[] parts)
    {
        var values = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return values.Length == 0 ? "" : string.Join(" · ", values);
    }

    /// <summary>优先查本地化文本；card_xxx_Name 再退回卡牌数据。</summary>
    private string DisplayName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var localized = _loc.Get(name);
        if (!string.IsNullOrEmpty(localized)) return localized;

        var code = EncounterKeys.FromOpponentName(name);
        if (code is not null)
        {
            var cardName = _loc.GetCardName(code)
                           ?? (_cards.TryGet(code, out var info) ? info.Name : null);
            return string.IsNullOrEmpty(cardName) ? "电脑对手" : cardName;
        }

        if (name.Contains("_Name", StringComparison.OrdinalIgnoreCase)) return "电脑对手";
        return name;
    }

    /// <summary>
    /// 绑定关卡录制：以"节点名称 key"作为稳定键（Boss 名称 key → 本地化名称），
    /// 不按节点顺序。PvP 名字不会产生 key。
    /// </summary>
    private void BindEncounter(string? opponentName)
    {
        if (!_config.RecordBossDecks) return;

        var championKey = EncounterKeys.FromOpponentName(opponentName);
        var nameKey = _logWatcher.EnemyNameKey;
        if (string.IsNullOrWhiteSpace(nameKey) && championKey is not null)
        {
            nameKey = $"card_{championKey}_Name";
        }

        if (string.IsNullOrWhiteSpace(nameKey))
        {
            if (_tracker.OpponentEncounterKey is not null)
            {
                _encounterLabel = "";
                _encounterCampaign = "";
                _encounterMapName = "";
                _encounterNodeName = "";
                _tracker.SetEncounter(null, null);
                _committedThisGame = false;
            }

            return;
        }

        _encounterLabel = _loc.Get(nameKey)
                          ?? (championKey is not null ? _loc.GetCardName(championKey) : null)
                          ?? (championKey is not null ? _cards.GetName(championKey) : nameKey);

        _encounterNodeName = _encounterLabel;

        var adventureKey = _gameData.GetCampaignKey(_logWatcher.AdventureGuid);
        if (!LocalizationDatabase.IsMapKey(adventureKey))
        {
            // 统计文本之类的 key 不能用来区分地图
            adventureKey = null;
        }

        _encounterCampaign = _loc.GetCampaignName(adventureKey) ?? "";
        _encounterMapName = _loc.GetMapName(adventureKey) ?? "";

        // 录制键 = 战役 + 大地图（冒险 key） + Boss，同名地图在不同战役下也能分开
        var encounterKey = EncounterKeys.Compose(adventureKey, nameKey);
        if (string.Equals(encounterKey, _tracker.OpponentEncounterKey, StringComparison.Ordinal)) return;

        // 旧格式数据迁移会写入录制文件，普通版与网络源只读时跳过
        var record = _encounters.Get(encounterKey)
                     ?? (CanWriteEncounters
                         ? _encounters.AdoptLegacy(nameKey, encounterKey, _encounterCampaign, _encounterMapName, _encounterNodeName)
                         : null);
        _tracker.SetEncounter(encounterKey, record);
        _committedThisGame = false;
    }

    public string BuildDiagnostics()
    {
        var lastRead = _logWatcher.LastReadUtc == default
            ? "-"
            : _logWatcher.LastReadUtc.ToLocalTime().ToString("HH:mm:ss");

        return string.Join("\n", new[]
        {
            $"实时日志：{_logWatcher.CurrentLogPath ?? "(未找到，游戏未运行?)"}",
            $"最近读取：{lastRead}",
            $"对手名称 key：{_logWatcher.EnemyNameKey ?? "-"}",
            $"冒险 GUID：{_logWatcher.AdventureGuid ?? "-"}",
            $"节点序号：{_logWatcher.NodeIndex?.ToString() ?? "-"}",
            $"战役名称 key：{_gameData.GetCampaignKey(_logWatcher.AdventureGuid) ?? "-"}",
            $"战役名称：{_encounterCampaign}",
            $"大地图：{_encounterMapName}",
            $"节点名称：{_encounterNodeName}",
            $"本地化条目：{_loc.Count}",
            $"卡牌数据：{_cards.Count}",
            $"当前关卡键：{_tracker.OpponentEncounterKey ?? "-"}"
        });
    }

    private void RefreshRows(bool force = false)
    {
        if (!force && _tracker.Revision == _lastRevision)
        {
            return;
        }

        _lastRevision = _tracker.Revision;

        HasDeck = _tracker.HasDeck;
        DeckTitle = _tracker.HasDeck
            ? $"我的牌库   {_tracker.TotalRemaining} / {_tracker.TotalInitial}"
            : "我的牌库";

        if (_tracker.HasDeck)
        {
            var rows = _tracker.InitialDeck
                .Select(pair => CreateRow(pair.Key, _tracker.Remaining.GetValueOrDefault(pair.Key), pair.Value))
                .Where(row => row is not null)
                .Select(row => row!)
                .Where(row => !_config.HideExhausted || row.Count > 0)
                .OrderBy(row => row.Cost < 0 ? 99 : row.Cost)
                .ThenBy(row => row.Name, StringComparer.CurrentCulture);

            SetRows(DeckRows, rows);
        }
        else
        {
            SetRows(DeckRows, Array.Empty<CardRow>());
        }

        // Boss 牌库（历史录制 - 本局已出牌）
        ShowBossSection = _config.RecordBossDecks && _tracker.IsInGame && HasCurrentEncounter;
        if (ShowBossSection)
        {
            var record = _tracker.Encounter;
            var name = record is not null ? DisplayNameOf(record) : JoinName(_encounterCampaign, _encounterNodeName);
            var finalized = record?.IsFinalized == true ? " · 已定档（不再自动录制）" : "";
            if (record is not null && record.Cards.Count > 0)
            {
                // 英雄法术完全不扣；武器会回手反复打出，最多只扣 1 张实体
                var remaining = _tracker.BuildBossRemaining(IsChampionSpell, IsEquipment);

                var bossRows = remaining
                    .Select(pair => CreateRow(pair.Key, pair.Value, pair.Value))
                    .Where(row => row is not null)
                    .Select(row => row!)
                    .ToList();

                // 被手动「已去除」扣到 0 的牌仍保留一行（灰色 ×0），方便右键撤销
                foreach (var (code, total) in record.Cards)
                {
                    if (remaining.ContainsKey(code)) continue;
                    if (_tracker.ManualRemovedCount(code) <= 0) continue;
                    if (CreateRow(code, 0, total) is { } zeroRow) bossRows.Add(zeroRow);
                }

                SetRows(BossRows, bossRows
                    .OrderBy(row => row.Cost < 0 ? 99 : row.Cost)
                    .ThenBy(row => row.Name, StringComparer.CurrentCulture));

                BossTitle = $"Boss 牌库   剩余 {remaining.Values.Sum()} / {record.Cards.Values.Sum()}";
                var expected = record.ExpectedCount > 0
                    ? $" · 预定 {record.ExpectedCount} 张 / 已录制 {record.Cards.Values.Sum()} 张"
                    : "";
                BossSubtitle = $"{name} · 已录制 {record.Cards.Count} 种牌{expected}{finalized}";
            }
            else
            {
                // 该 Boss 还没有录制：清掉上一场遗留的 Boss 行，只显示提示
                SetRows(BossRows, Array.Empty<CardRow>());
                var expected = record is { ExpectedCount: > 0 } ? $" · 预定 {record.ExpectedCount} 张" : "";
                if (CanWriteEncounters && record?.IsFinalized != true)
                {
                    BossTitle = "Boss 牌库   录制中";
                    BossSubtitle = $"{name} · 本局已记录 {_tracker.TotalOpponentPlayed} 张，战斗结束后自动存档{expected}";
                }
                else
                {
                    BossTitle = "Boss 牌库   暂无录制";
                    var reason = record?.IsFinalized == true
                        ? "已定档牌库"
                        : _encounters.IsLocalView ? "普通版不写入牌库录制" : $"数据源「{_encounters.ViewSourceName}」只读";
                    BossSubtitle = $"{name} · 本局已记录 {_tracker.TotalOpponentPlayed} 张 · {reason}{expected}{finalized}";
                }
            }
        }
        else
        {
            SetRows(BossRows, Array.Empty<CardRow>());
        }

        HasBossRows = BossRows.Count > 0;

        SetRows(OpponentRows, _tracker.OpponentPlayed
            .Select(pair => CreateRow(pair.Key, pair.Value, pair.Value,
                markedToken: _tracker.Encounter?.IsToken(pair.Key) == true,
                isNew: _tracker.Encounter?.Cards.ContainsKey(pair.Key) != true))
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderBy(row => row.Cost < 0 ? 99 : row.Cost)
            .ThenBy(row => row.Name, StringComparer.CurrentCulture));

        HasOpponentRows = OpponentRows.Count > 0;

        // 括号里是「非衍生牌」张数（手动标记为衍生的牌和自带衍生卡不算），后面是所有牌总数
        var nonTokenPlayed = _tracker.OpponentPlayed
            .Where(pair => !IsGeneratedCard(pair.Key) && _tracker.Encounter?.IsToken(pair.Key) != true)
            .Sum(pair => pair.Value);
        OpponentTitle = $"对手已打出   ({nonTokenPlayed}) {_tracker.TotalOpponentPlayed}";

        if (!_tracker.HasDeck)
        {
            SetRows(SeenRows, _tracker.LocalSeen
                .Select(pair => CreateRow(pair.Key, pair.Value, pair.Value))
                .Where(row => row is not null)
                .Select(row => row!)
                .OrderBy(row => row.Cost < 0 ? 99 : row.Cost)
                .ThenBy(row => row.Name, StringComparer.CurrentCulture));
        }
        else
        {
            SetRows(SeenRows, Array.Empty<CardRow>());
        }

        HasSeenRows = SeenRows.Count > 0;
        SeenTitle = $"本局已见   {SeenRows.Count}";

        ShowEmptyHint = !HasDeck && !HasSeenRows;
        EmptyHint = _inGame ? "正在等待卡牌出现…" : "进入对局后自动开始记录";
    }

    /// <summary>
    /// 原地同步列表：按卡牌代码复用已有行，只更新数量、移动位置或增删差异项。
    /// 避免每次刷新都重建所有行，否则悬停中的预览会被反复关闭（一闪一闪）。
    /// </summary>
    private static void SetRows(ObservableCollection<CardRow> target, IEnumerable<CardRow> rows)
    {
        var desired = rows.ToList();
        if (desired.Count == 0)
        {
            if (target.Count > 0) target.Clear();
            return;
        }

        var existing = new Dictionary<string, CardRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in target) existing[row.Code] = row;

        var final = new List<CardRow>(desired.Count);
        foreach (var row in desired)
        {
            if (existing.TryGetValue(row.Code, out var reused))
            {
                reused.Count = row.Count;
                reused.Total = row.Total;
                reused.IsToken = row.IsToken;
                reused.IsNew = row.IsNew;
                if (row.ImageSource is not null)
                {
                    reused.ImageSource = row.ImageSource;
                    reused.ImageRequested = true;
                }
                else if (reused.ImageSource is null)
                {
                    // 本地暂时没有图片时允许下次悬停重试
                    reused.ImageRequested = false;
                }

                final.Add(reused);
            }
            else
            {
                existing[row.Code] = row;
                final.Add(row);
            }
        }

        for (var i = 0; i < final.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], final[i])) continue;

            var index = target.IndexOf(final[i]);
            if (index >= 0) target.Move(index, i);
            else if (i < target.Count) target.Insert(i, final[i]);
            else target.Add(final[i]);
        }

        while (target.Count > final.Count) target.RemoveAt(target.Count - 1);
    }

    private CardRow? CreateRow(string code, int count, int total,
        bool markedToken = false, bool isNew = false)
    {
        var info = FindCardInfo(code);

        // Data Dragon 没有的新卡（如活动/PvE 专属卡）用游戏自带本地化补齐名称与说明
        var name = info?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = _loc.GetCardName(code);
        if (string.IsNullOrWhiteSpace(name)) name = code;

        var cost = info?.Cost ?? -1;

        var tooltipParts = new List<string> { name };
        // 官方数据没有描述时（游戏独占卡 / 本地化兜底卡）用游戏文本补齐
        var description = TagRegex.Replace(info?.Description ?? "", "").Trim();
        if (description.Length == 0) description = CleanLocText(_loc.Get($"card_{code}_Description"));

        if (info is not null)
        {
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.Type)) meta.Add(info.Type);
            if (!string.IsNullOrWhiteSpace(info.Region)) meta.Add(info.Region);
            if (!string.IsNullOrWhiteSpace(info.Rarity)) meta.Add(info.Rarity);
            if (meta.Count > 0) tooltipParts.Add(string.Join(" · ", meta));

            if (description.Length > 0) tooltipParts.Add(description);
            if (info.Keywords.Count > 0) tooltipParts.Add(string.Join(" / ", info.Keywords));
        }
        else
        {
            if (description.Length > 0) tooltipParts.Add(description);
            else tooltipParts.Add($"未收录卡牌代码：{code}");
        }

        var isToken = markedToken || IsGeneratedCard(code);
        if (IsEquipment(code))
        {
            tooltipParts.Add("武器 / 装备：装备单位阵亡后回手，可反复打出；牌库只按 1 张实体计。");
        }

        return new CardRow
        {
            Code = code,
            Name = name,
            Cost = cost,
            Count = count,
            Total = total,
            RegionBrush = RegionPalette.Get(info?.RegionRef ?? ""),
            IsToken = isToken,
            IsNew = isNew && !isToken,
            Tooltip = string.Join("\n", tooltipParts),
            ImageSource = TryGetCachedCardImage(code)
        };
    }

    /// <summary>清理游戏本地化文本里的标记，让悬停提示可读。</summary>
    private static string CleanLocText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var text = value
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<nobr>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("</nobr>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("<nobr/>", "", StringComparison.OrdinalIgnoreCase);

        text = TagRegex.Replace(text, "");
        text = LocTagRegex.Replace(text, "");
        text = LocValueRegex.Replace(text, "$1");
        text = LocPlaceholderRegex.Replace(text, "");
        return text.Trim();
    }

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        CommitCurrentGame();
        _logWatcher.Dispose();
        _timer.Stop();
        _api.Dispose();
    }
}
