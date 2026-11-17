using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace LoRPoCTracker.Models;

/// <summary>Boss 牌库编辑窗口中的一行。</summary>
public sealed class CardEditRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public int Cost { get; init; }

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public string CountText => Count.ToString();
    public string CostText => Cost < 0 ? "-" : Cost.ToString();

    /// <summary>地区颜色条，与主窗口卡牌行左侧一致。</summary>
    public Brush RegionBrush { get; init; } = Brushes.Gray;

    /// <summary>衍生卡（诅咒 / 不可收集衍生物），不写回 Boss 牌库。</summary>
    public bool IsToken { get; init; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
