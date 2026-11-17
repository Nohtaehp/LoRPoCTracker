using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace LoRPoCTracker.Models;

/// <summary>
/// 记牌器「本地图 Boss 牌库」下拉中的一行：一个已录入的地图 Boss 及其牌库。
/// </summary>
public sealed class MapBossRow : INotifyPropertyChanged
{
    /// <summary>录制键（adventureKey|card_xxx_Name）。</summary>
    public string Key { get; init; } = "";

    /// <summary>Boss 英雄卡代码（头像取自该卡的插画）。</summary>
    public string BossCode { get; init; } = "";

    /// <summary>显示名：地图节点名优先，其次是卡牌名。</summary>
    public string Name { get; init; } = "";

    /// <summary>副标题：几种牌 / 共几张。</summary>
    public string Detail { get; init; } = "";

    /// <summary>右侧张数。</summary>
    public string CountText { get; init; } = "";

    /// <summary>右上角标记：本局 / 已定档。</summary>
    public string Badge { get; init; } = "";

    /// <summary>该 Boss 牌库是否已归档（定档）：行背景会稍微区分。</summary>
    public bool IsFinalized { get; init; }

    public Visibility BadgeVisibility =>
        string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>无头像时圆形占位底色（阵营色）。</summary>
    public Brush AvatarBrush { get; init; } = System.Windows.Media.Brushes.Gray;

    /// <summary>无头像时显示的首字。</summary>
    public string Initial { get; init; } = "";

    /// <summary>牌库明细（展开后显示）。</summary>
    public ObservableCollection<CardRow> Cards { get; } = new();

    private ImageSource? _avatar;

    /// <summary>Boss 头像（与游戏地图节点同源的插画裁切）。</summary>
    public ImageSource? Avatar
    {
        get => _avatar;
        set
        {
            if (ReferenceEquals(_avatar, value)) return;
            _avatar = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InitialVisibility)));
        }
    }

    public Visibility AvatarVisibility => _avatar is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility InitialVisibility => _avatar is null ? Visibility.Visible : Visibility.Collapsed;

    private bool _isExpanded;

    /// <summary>是否展开该 Boss 的牌库明细。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandGlyph)));
        }
    }

    public string ExpandGlyph => _isExpanded ? "▾" : "▸";

    public event PropertyChangedEventHandler? PropertyChanged;
}
