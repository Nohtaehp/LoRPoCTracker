using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LoRPoCTracker.Models;
using LoRPoCTracker.Services;
using LoRPoCTracker.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;

namespace LoRPoCTracker.Views;

public partial class CardEditorWindow : Window
{
    private readonly OverlayViewModel _viewModel;
    private readonly ObservableCollection<CardEditRow> _rows;
    private readonly HashSet<string> _deletedCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _encounterKey;
    private string? _previewCode;
    private System.Windows.Threading.DispatcherTimer? _syncTimer;

    /// <summary>编辑当前对局的 Boss 牌库。</summary>
    public CardEditorWindow(OverlayViewModel viewModel) : this(viewModel, null)
    {
    }

    /// <summary>编辑指定 key 的录制（录制管理窗口用）；key 为空时编辑当前对局。</summary>
    public CardEditorWindow(OverlayViewModel viewModel, string? encounterKey)
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);

        _viewModel = viewModel;

        EncounterRecord? record;
        if (string.IsNullOrWhiteSpace(encounterKey))
        {
            record = viewModel.EnsureCurrentEncounter();
            _encounterKey = viewModel.CurrentEncounterKey ?? "";
            if (record is null)
            {
                System.Windows.MessageBox.Show("当前不在可编辑的对局中。", "编辑 Boss 牌库",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        else
        {
            _encounterKey = encounterKey;
            record = viewModel.GetEncounter(encounterKey);
            if (record is null)
            {
                System.Windows.MessageBox.Show("该关卡的录制已不存在。", "编辑 Boss 牌库",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        if (record is null)
        {
            Close();
            _rows = new ObservableCollection<CardEditRow>();
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(encounterKey)
            ? viewModel.CurrentEncounterDisplayName
            : viewModel.GetEncounterDisplayName(encounterKey);

        _ = LoadBossAvatarAsync(record);

        // 对局中编辑当前 Boss：把本局对手已打出的牌单向并入牌库，
        // 新出现的卡不用等战斗结束就能补录；已标记为衍生的卡不同步。
        var cards = new Dictionary<string, int>(record.Cards, StringComparer.OrdinalIgnoreCase);
        if (viewModel.IsInGame &&
            string.Equals(_encounterKey, viewModel.CurrentEncounterKey, StringComparison.Ordinal))
        {
            foreach (var (code, count) in viewModel.CurrentOpponentPlayed)
            {
                if (record.IsToken(code)) continue;
                cards[code] = Math.Max(cards.GetValueOrDefault(code), count);
            }
        }

        _rows = new ObservableCollection<CardEditRow>(
            cards
                .Select(pair => viewModel.CreateEditRow(pair.Key, pair.Value))
                .OrderBy(row => row.Cost < 0 ? 99 : row.Cost)
                .ThenBy(row => row.Name, StringComparer.CurrentCulture));

        if (record.ExpectedCount > 0) ExpectedBox.Text = record.ExpectedCount.ToString();

        foreach (var row in _rows) row.PropertyChanged += OnRowPropertyChanged;
        _rows.CollectionChanged += OnRowsCollectionChanged;
        UpdateSummary();

        DeckList.ItemsSource = _rows;
        SearchResults.ItemsSource = Array.Empty<CardInfo>();

        // 对局中打开当前 Boss：对手新打出的牌持续单向补进列表。
        if (viewModel.IsInGame &&
            string.Equals(_encounterKey, viewModel.CurrentEncounterKey, StringComparison.Ordinal))
        {
            _syncTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _syncTimer.Tick += (_, _) => SyncPlayedCards();
            _syncTimer.Start();
            Closed += (_, _) => _syncTimer?.Stop();
        }
    }

    /// <summary>
    /// 顶部显示 Boss 头像：与游戏地图节点一致，取该 Boss 英雄卡的插画裁切；
    /// 本地没有插画时回退官方卡图，仍没有则显示阵营色圆 + 首字。
    /// </summary>
    private async Task LoadBossAvatarAsync(EncounterRecord record)
    {
        var code = EncounterKeys.FromOpponentName(record.BossKey) ?? "";
        if (string.IsNullOrWhiteSpace(code)) return;

        var displayName = string.IsNullOrWhiteSpace(record.NodeName) ? TitleText.Text : record.NodeName;
        var placeholder = _viewModel.CreateEditRow(code, 1);

        BossAvatarCircle.Fill = placeholder.RegionBrush;
        BossAvatarInitial.Text = string.IsNullOrWhiteSpace(displayName)
            ? "?"
            : char.ToUpperInvariant(displayName.Trim()[0]).ToString();

        var image = await _viewModel.LoadBossAvatarAsync(code);
        if (image is null) return;

        var brush = new System.Windows.Media.ImageBrush(image)
        {
            Stretch = System.Windows.Media.Stretch.UniformToFill
        };
        brush.Freeze();

        BossAvatarEllipse.Fill = brush;
        BossAvatarEllipse.Visibility = Visibility.Visible;
        BossAvatarInitial.Visibility = Visibility.Collapsed;
    }

    /// <summary>把对手新打出、尚未在列表中的牌补进来（已标记为衍生的除外）。</summary>
    private void SyncPlayedCards()
    {
        if (!_viewModel.IsInGame ||
            !string.Equals(_encounterKey, _viewModel.CurrentEncounterKey, StringComparison.Ordinal))
        {
            return;
        }

        var record = _viewModel.GetEncounter(_encounterKey);
        if (record is null) return;

        foreach (var (code, count) in _viewModel.CurrentOpponentPlayed)
        {
            if (record.IsToken(code)) continue;
            if (_deletedCodes.Contains(code)) continue;

            var existing = _rows.FirstOrDefault(row =>
                string.Equals(row.Code, code, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // 只向上同步：本局打出更多时补到实际数量
                if (count > existing.Count) existing.Count = count;
                continue;
            }

            _rows.Add(_viewModel.CreateEditRow(code, count));
        }
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (CardEditRow row in e.NewItems) row.PropertyChanged += OnRowPropertyChanged;
        }

        UpdateSummary();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardEditRow.Count)) UpdateSummary();
    }

    /// <summary>顶部显示牌库总览：共几种、共几张；有预定张数时给出差值。</summary>
    private void UpdateSummary()
    {
        var total = _rows.Sum(row => row.Count);
        SummaryText.Text = $"共 {_rows.Count} 种 · {total} 张";
        UpdateExpectedHint(total);
    }

    private void UpdateExpectedHint(int total)
    {
        var expected = ParseExpectedCount();
        if (expected <= 0)
        {
            ExpectedHint.Text = "填 0 或留空表示不设置";
            return;
        }

        var diff = total - expected;
        ExpectedHint.Text = diff == 0
            ? $"与实际录制一致（{total} 张）"
            : $"实际录制 {total} 张，相差 {(diff > 0 ? "+" : "")}{diff} 张";
    }

    private int ParseExpectedCount() =>
        int.TryParse(ExpectedBox.Text.Trim(), out var value) && value > 0 ? value : 0;

    private void ExpectedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;

        UpdateExpectedHint(_rows.Sum(row => row.Count));
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code }) return;

        var row = _rows.FirstOrDefault(item => item.Code == code);
        if (row is null) return;

        _rows.Remove(row);
        _deletedCodes.Add(code);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunSearch();
    }

    private void RunSearch()
    {
        SearchResults.ItemsSource = _viewModel.SearchCards(SearchBox.Text);
    }

    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelected();

    private async void DeckList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeckList.SelectedItem is CardEditRow row) await ShowPreviewAsync(row.Code, row.Name);
    }

    private async void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is CardInfo card) await ShowPreviewAsync(card.Code, card.Name);
    }

    private async Task ShowPreviewAsync(string code, string name)
    {
        _previewCode = code;
        PreviewName.Text = _viewModel.HasOfficialImage(code)
            ? name
            : name + "（官方未收录 · 本地拼合卡面）";
        PreviewImage.Source = null;

        if (!_viewModel.ShowCardImages)
        {
            PreviewHint.Text = "图片展示未开启（主菜单可开启“显示卡牌图片”）";
            PreviewHint.Visibility = Visibility.Visible;
            return;
        }

        var image = _viewModel.TryGetCachedCardImage(code);
        if (image is null)
        {
            PreviewHint.Text = "正在下载图片…";
            PreviewHint.Visibility = Visibility.Visible;
            image = await _viewModel.LoadCardImageAsync(code);
        }

        if (!string.Equals(_previewCode, code, StringComparison.Ordinal)) return;

        if (image is null)
        {
            PreviewHint.Text = "暂无该卡图片，可用菜单「更新当前版本卡牌资源包…」重新导入";
            PreviewHint.Visibility = Visibility.Visible;
            return;
        }

        PreviewImage.Source = image;
        PreviewHint.Visibility = Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddSelected();

    private void AddSelected()
    {
        if (SearchResults.SelectedItem is not CardInfo card) return;

        if (_viewModel.IsGeneratedCard(card.Code))
        {
            System.Windows.MessageBox.Show("诅咒 / 衍生物不属于 Boss 牌库，已按衍生处理，不能加入。",
                "编辑 Boss 牌库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var count = int.TryParse(AddCountBox.Text, out var parsed) && parsed > 0 ? parsed : 1;
        var existing = _rows.FirstOrDefault(row => row.Code == card.Code);
        if (existing is not null)
        {
            existing.Count += count;
        }
        else
        {
            _rows.Add(_viewModel.CreateEditRow(card.Code, count));
            _deletedCodes.Remove(card.Code);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            // 衍生卡（诅咒 / 不可收集 / 手动标记）不写回录制
            if (row.IsToken || _viewModel.IsEncounterToken(_encounterKey, row.Code)) continue;
            _viewModel.SetEncounterCard(_encounterKey, row.Code, row.Count);
        }

        foreach (var code in _deletedCodes)
        {
            _viewModel.SetEncounterCard(_encounterKey, code, 0);
        }

        _viewModel.SetEncounterExpectedCount(_encounterKey, ParseExpectedCount());
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
