namespace LoRPoCTracker.Models;

/// <summary>关卡管理窗口中的一行。</summary>
public sealed class EncounterEditRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Key { get; init; } = "";

    /// <summary>Boss 名称 key（组合键最后一段），用于界面显示。</summary>
    public string BossKey { get; init; } = "";

    private string _campaign = "";
    public string Campaign
    {
        get => _campaign;
        set
        {
            if (_campaign == value) return;
            _campaign = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Campaign)));
        }
    }

    private string _mapName = "";

    public string MapName
    {
        get => _mapName;
        set
        {
            if (_mapName == value) return;
            _mapName = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MapName)));
        }
    }

    public string NodeName { get; init; } = "";

    private int _cardCount;
    public int CardCount
    {
        get => _cardCount;
        set
        {
            if (_cardCount == value) return;
            _cardCount = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CardCount)));
        }
    }

    private int _cardTotal;
    /// <summary>牌库总张数（各卡牌数量之和）。</summary>
    public int CardTotal
    {
        get => _cardTotal;
        set
        {
            if (_cardTotal == value) return;
            _cardTotal = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CardTotal)));
        }
    }

    private int _expectedCount;
    /// <summary>预定牌库总张数（0 表示未设置）。</summary>
    public int ExpectedCount
    {
        get => _expectedCount;
        set
        {
            if (_expectedCount == value) return;
            _expectedCount = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ExpectedCount)));
        }
    }

    private bool _isFinalized;
    /// <summary>是否已定档（定档后不再自动录制）。</summary>
    public bool IsFinalized
    {
        get => _isFinalized;
        set
        {
            if (_isFinalized == value) return;
            _isFinalized = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFinalized)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FinalizedText)));
        }
    }

    /// <summary>列表「定档」列的显示文字。</summary>
    public string FinalizedText => IsFinalized ? "已定档" : "—";

    private IReadOnlyList<string> _mapOptions = Array.Empty<string>();

    /// <summary>当前战役可选的大地图（战役改变时更新）。</summary>
    public IReadOnlyList<string> MapOptions
    {
        get => _mapOptions;
        set
        {
            if (ReferenceEquals(_mapOptions, value)) return;
            _mapOptions = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MapOptions)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<string> CampaignOptions { get; init; } = Array.Empty<string>();
}
