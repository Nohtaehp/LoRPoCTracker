using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LoRPoCTracker.Models;
using LoRPoCTracker.Services;
using LoRPoCTracker.ViewModels;

namespace LoRPoCTracker.Views;

public partial class EncounterEditorWindow : Window
{
    private readonly OverlayViewModel _viewModel;
    private readonly ObservableCollection<EncounterEditRow> _rows;

    public EncounterEditorWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);

        _viewModel = viewModel;

        if (!viewModel.CanWriteEncounters)
        {
            var message = viewModel.IsAdminMode
                ? "当前为只读网络源，牌库录制 / 编辑已暂停。请在菜单「牌库数据源」切回「本地录制」后再管理。"
                : "关卡录制管理仅管理员版可用。请在菜单「软件版本」中切换。";
            _rows = new ObservableCollection<EncounterEditRow>();
            Loaded += (_, _) =>
            {
                System.Windows.MessageBox.Show(message, "关卡录制管理",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            };
            return;
        }

        var campaigns = viewModel.GetCampaignOptions();
        var mapsByCampaign = viewModel.GetMapOptionsByCampaign();
        var noMaps = Array.Empty<string>();

        _rows = new ObservableCollection<EncounterEditRow>(
            viewModel.GetAllEncounters()
                .OrderBy(record => string.IsNullOrWhiteSpace(record.Campaign) ? 1 : 0)
                .ThenBy(record => record.Campaign, StringComparer.CurrentCulture)
                .ThenBy(record => record.MapName, StringComparer.CurrentCulture)
                .ThenBy(record => record.NodeName, StringComparer.CurrentCulture)
                .Select(record =>
                {
                    var row = new EncounterEditRow
                    {
                        Key = record.Key,
                        BossKey = record.BossKey,
                        Campaign = record.Campaign,
                        MapName = record.MapName,
                        NodeName = record.NodeName,
                        CardCount = record.Cards.Count,
                        CardTotal = record.Cards.Values.Sum(),
                        ExpectedCount = record.ExpectedCount,
                        IsFinalized = record.IsFinalized,
                        CampaignOptions = campaigns,
                        MapOptions = mapsByCampaign.TryGetValue(record.Campaign, out var maps) ? maps : noMaps
                    };

                    row.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName != nameof(EncounterEditRow.Campaign)) return;

                        row.MapOptions = mapsByCampaign.TryGetValue(row.Campaign, out var options)
                            ? options
                            : noMaps;
                    };

                    return row;
                }));

        var view = new ListCollectionView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EncounterEditRow.Campaign)));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EncounterEditRow.MapName)));
        if (view is ICollectionViewLiveShaping live && live.CanChangeLiveGrouping == true)
        {
            live.LiveGroupingProperties.Add(nameof(EncounterEditRow.Campaign));
            live.LiveGroupingProperties.Add(nameof(EncounterEditRow.MapName));
            live.IsLiveGrouping = true;
        }

        RecordList.ItemsSource = view;
        RecordList.PreviewMouseLeftButtonDown += RecordList_PreviewMouseLeftButtonDown;
        UpdateFinalizeButton();
    }

    private void RecordList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFinalizeButton();

    /// <summary>按钮文字跟随选中行的定档状态。</summary>
    private void UpdateFinalizeButton()
    {
        FinalizeButton.Content = RecordList.SelectedItem is EncounterEditRow row && row.IsFinalized
            ? "取消定档"
            : "定档牌库";
    }

    /// <summary>定档 / 取消定档：定档后该关卡不再自动录制，牌库仍可编辑。</summary>
    private void Finalize_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsAdminMode) return;

        if (RecordList.SelectedItem is not EncounterEditRow row)
        {
            System.Windows.MessageBox.Show("请先选择一条录制。", "定档牌库",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var target = !row.IsFinalized;
        if (target)
        {
            var answer = System.Windows.MessageBox.Show(
                $"确定将该关卡的牌库定档？\n\n{row.BossKey}\n\n" +
                "定档后以后到这个关卡都不再自动录制（不合并本局出牌），" +
                "但「编辑牌库」仍可随时手动修改。",
                "定档牌库", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        _viewModel.SetEncounterFinalized(row.Key, target);
        row.IsFinalized = target;
        UpdateFinalizeButton();
    }

    /// <summary>点击战役/大地图分组标题时切换收起/展开。</summary>
    private void RecordList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        var toggle = FindAncestor<ToggleButton>(source);
        if (toggle is null || toggle.DataContext is not CollectionViewGroup) return;

        toggle.IsChecked = !(toggle.IsChecked ?? false);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void EditDeck_Click(object sender, RoutedEventArgs e)
    {
        if (RecordList.SelectedItem is not EncounterEditRow row)
        {
            System.Windows.MessageBox.Show("请先选择一条录制。", "编辑牌库",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = new CardEditorWindow(_viewModel, row.Key);
        editor.ShowDialog();

        var record = _viewModel.GetEncounter(row.Key);
        if (record is not null)
        {
            row.CardCount = record.Cards.Count;
            row.CardTotal = record.Cards.Values.Sum();
            row.ExpectedCount = record.ExpectedCount;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            _viewModel.SetEncounterInfo(row.Key, row.Campaign, row.MapName);
            _viewModel.SetEncounterExpectedCount(row.Key, row.ExpectedCount);
            _viewModel.SetEncounterFinalized(row.Key, row.IsFinalized);
        }

        Close();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RecordList.SelectedItem is not EncounterEditRow row) return;

        _viewModel.DeleteEncounter(row.Key);
        _rows.Remove(row);
        UpdateFinalizeButton();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
