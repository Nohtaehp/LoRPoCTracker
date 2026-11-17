using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace LoRPoCTracker.Models;

/// <summary>界面上一行卡牌。</summary>
public sealed class CardRow : INotifyPropertyChanged
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExhausted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowOpacity)));
        }
    }

    private int _total;
    public int Total
    {
        get => _total;
        set
        {
            if (_total == value) return;
            _total = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Total)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public bool IsExhausted => Count <= 0;

    public string CostText => Cost < 0 ? "-" : Cost.ToString();
    public string CountText => ShowTotal ? $"{Count} / {Total}" : $"×{Count}";

    public bool ShowTotal { get; init; }

    private bool _isToken;

    /// <summary>是否为衍生物（不可收集，或被手动标记为衍生）。</summary>
    public bool IsToken
    {
        get => _isToken;
        set
        {
            if (_isToken == value) return;
            _isToken = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsToken)));
        }
    }

    private bool _isNew;

    /// <summary>是否为录制牌库中没有的新牌（仅「对手已打出」列表使用）。</summary>
    public bool IsNew
    {
        get => _isNew;
        set
        {
            if (_isNew == value) return;
            _isNew = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNew)));
        }
    }

    public Brush RegionBrush { get; init; } = Brushes.Gray;

    public string Tooltip { get; init; } = "";

    public double RowOpacity => IsExhausted ? 0.32 : 1.0;

    private ImageSource? _imageSource;

    /// <summary>卡牌图片（开启图片展示且已下载/缓存后才有值）。</summary>
    public ImageSource? ImageSource
    {
        get => _imageSource;
        set
        {
            if (ReferenceEquals(_imageSource, value)) return;
            _imageSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageVisibility)));
        }
    }

    public Visibility ImageVisibility => _imageSource is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>避免悬停时对同一张卡重复发起下载。</summary>
    public bool ImageRequested { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class RegionPalette
{
    private static readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Demacia"] = Color.FromRgb(0xC8, 0xB2, 0x6E),
        ["Freljord"] = Color.FromRgb(0x6F, 0xB3, 0xE0),
        ["Ionia"] = Color.FromRgb(0xE0, 0x86, 0x9B),
        ["Noxus"] = Color.FromRgb(0xC7, 0x4B, 0x3F),
        ["PiltoverZaun"] = Color.FromRgb(0xE0, 0xA6, 0x4E),
        ["ShadowIsles"] = Color.FromRgb(0x54, 0xB8, 0xA8),
        ["Bilgewater"] = Color.FromRgb(0x3A, 0xA0, 0x9E),
        ["Targon"] = Color.FromRgb(0x9A, 0x86, 0xD8),
        ["Shurima"] = Color.FromRgb(0xD9, 0xB4, 0x5E),
        ["BandleCity"] = Color.FromRgb(0x7D, 0xC7, 0x71),
        ["Runeterra"] = Color.FromRgb(0x9F, 0xA8, 0xB6)
    };

    public static Brush Get(string regionRef)
    {
        var brush = new SolidColorBrush(GetColor(regionRef));
        brush.Freeze();
        return brush;
    }

    /// <summary>区域主色（未知区域回退为灰色）。</summary>
    public static Color GetColor(string regionRef)
    {
        if (!string.IsNullOrEmpty(regionRef) && Colors.TryGetValue(regionRef, out var color))
        {
            return color;
        }

        return Color.FromRgb(0x8A, 0x93, 0xA0);
    }

    /// <summary>
    /// 从 99 系列卡牌代码解析区域：固定格式为「99 + 两个大写英文字母」。
    /// NE/NU 为中立怪/中立法术，归到 Runeterra；未知区域返回 null。
    /// </summary>
    public static string? RegionRefFromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length < 5) return null;
        if (!code.StartsWith("99", StringComparison.OrdinalIgnoreCase)) return null;

        return code.Substring(2, 2).ToUpperInvariant() switch
        {
            "BC" => "BandleCity",
            "BW" => "Bilgewater",
            "DE" => "Demacia",
            "FR" => "Freljord",
            "IO" => "Ionia",
            "MT" => "Targon",
            "NX" => "Noxus",
            "PZ" => "PiltoverZaun",
            "SH" => "Shurima",
            "SI" => "ShadowIsles",
            "RU" => "Runeterra",
            "NE" => "Runeterra",
            "NU" => "Runeterra",
            _ => null
        };
    }
}
