using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LoRPoCTracker.Models;
using LoRPoCTracker.Services;
using LoRPoCTracker.ViewModels;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LoRPoCTracker.Views;

/// <summary>
/// 单 Boss 牌库的导出 / 导入，以及把网络源的已归档牌库增量合并到本地。
/// 导出：仅录制模式（管理员版）且该 Boss 已归档（定档）；
/// 导入 / 合并：普通版与录制模式都可用，写入本地录制。
/// </summary>
public partial class DeckTransferWindow : Window
{
    private readonly OverlayViewModel _viewModel;
    private readonly ObservableCollection<DeckTransferRow> _rows = new();
    private bool _busy;

    public DeckTransferWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);

        _viewModel = viewModel;
        DeckList.ItemsSource = _rows;

        var sources = viewModel.GetNetworkSources();
        SourceBox.ItemsSource = sources;
        SourceBox.SelectedItem = sources.FirstOrDefault(source => source.Id == viewModel.ActiveSourceId)
                                 ?? sources.FirstOrDefault();

        LoadRows();
    }

    private void LoadRows(string? selectKey = null)
    {
        selectKey ??= (DeckList.SelectedItem as DeckTransferRow)?.Key;

        _rows.Clear();
        foreach (var record in _viewModel.GetLocalEncounters())
        {
            _rows.Add(new DeckTransferRow
            {
                Key = record.Key,
                Campaign = record.Campaign,
                MapName = record.MapName,
                NodeName = record.NodeName,
                BossKey = record.BossKey,
                CardCount = record.Cards.Count,
                CardTotal = record.Cards.Values.Sum(),
                IsFinalized = record.IsFinalized
            });
        }

        if (selectKey is not null)
        {
            DeckList.SelectedItem = _rows.FirstOrDefault(row =>
                string.Equals(row.Key, selectKey, StringComparison.Ordinal));
        }

        UpdateButtons();
    }

    private void DeckList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    /// <summary>按版本 / 选中行 / 归档状态刷新按钮与导出提示。</summary>
    private void UpdateButtons()
    {
        var row = DeckList.SelectedItem as DeckTransferRow;
        var admin = _viewModel.IsAdminMode;

        ExportButton.IsEnabled = !_busy && admin && row is { IsFinalized: true };
        ImportButton.IsEnabled = !_busy && row is not null;
        ImportFileButton.IsEnabled = !_busy;
        MergeButton.IsEnabled = !_busy && SourceBox.SelectedItem is DataSourceConfig;

        ExportHint.Text = !admin
            ? "导出：当前是普通版，导出只在录制模式（管理员版）可用。"
            : row is null
                ? "导出：请选择一条本地录制。"
                : row.IsFinalized
                    ? "导出：可导出选中的已归档牌库。"
                    : "导出：该 Boss 牌库尚未归档（定档），归档后才能导出。";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (DeckList.SelectedItem is not DeckTransferRow row)
        {
            System.Windows.MessageBox.Show("请先在列表中选择一个 Boss 牌库。", "导出 Boss 牌库",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var record = _viewModel.GetLocalEncounter(row.Key);
        if (record is null)
        {
            System.Windows.MessageBox.Show("该关卡的录制已不存在，请刷新后重试。", "导出 Boss 牌库",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 Boss 牌库",
            Filter = EncounterTransfer.FileFilter,
            DefaultExt = EncounterTransfer.FileExtension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = EncounterTransfer.BuildFileName(record)
        };
        if (dialog.ShowDialog(this) != true) return;

        var error = _viewModel.ExportDeck(row.Key, dialog.FileName);
        if (error is not null)
        {
            System.Windows.MessageBox.Show(error, "导出 Boss 牌库",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusText.Text = $"已导出「{row.DisplayName}」：{dialog.FileName}";
        System.Windows.MessageBox.Show(
            $"已导出「{row.DisplayName}」。\n文件包含 {row.CardCount} 种 / {row.CardTotal} 张牌。\n\n{dialog.FileName}",
            "导出 Boss 牌库", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>导入到当前选中的 Boss，文件里的关卡键会被忽略。</summary>
    private void ImportToSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (DeckList.SelectedItem is not DeckTransferRow row)
        {
            System.Windows.MessageBox.Show("请先在列表中选择要覆盖的 Boss 牌库。", "导入 Boss 牌库",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ImportDeck(row.Key);
    }

    /// <summary>按文件里记录的 Boss 导入（本地没有则新增）。</summary>
    private void ImportByFile_Click(object sender, RoutedEventArgs e) => ImportDeck(null);

    private void ImportDeck(string? targetKey)
    {
        if (_busy) return;

        var dialog = new OpenFileDialog
        {
            Title = "导入 Boss 牌库",
            Filter = EncounterTransfer.FileFilter,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        if (!EncounterTransfer.TryRead(dialog.FileName, out var deck, out var content) || deck is null)
        {
            System.Windows.MessageBox.Show(content, "导入 Boss 牌库",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var key = string.IsNullOrWhiteSpace(targetKey) ? deck.Key : targetKey;
        var local = _viewModel.GetLocalEncounter(key);
        var targetName = _viewModel.DescribeDeck(local ?? deck);

        var note = local is null
            ? "\n本地没有这个 Boss 的录制，将新增一条。"
            : local.IsFinalized
                ? "\n本地该 Boss 已归档，导入会覆盖牌库内容（归档状态保留）。"
                : "\n本地该 Boss 的录制（未归档）将被覆盖。";

        var text = $"文件：{Path.GetFileName(dialog.FileName)}\n" +
                   $"内容：{content}\n" +
                   $"文件里的 Boss：{_viewModel.DescribeDeck(deck)}\n" +
                   $"导入到：{targetName}{note}\n\n确定导入？";

        if (System.Windows.MessageBox.Show(text, "导入 Boss 牌库",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var message = _viewModel.ImportDeck(deck, key);
        StatusText.Text = message;
        LoadRows(key);

        System.Windows.MessageBox.Show(message, "导入 Boss 牌库",
            MessageBoxButton.OK, MessageBoxImage.Information);

        PromptSwitchToLocal();
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (SourceBox.SelectedItem is not DataSourceConfig source)
        {
            System.Windows.MessageBox.Show(
                "没有已配置文件地址的网络源。可先在主菜单「牌库数据源」中添加。",
                "合并网络源到本地", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (System.Windows.MessageBox.Show(
                $"将「{source.Name}」的牌库整体合并到本地录制：\n\n" +
                "· 只写入云端「已归档」且本地「未归档 / 本地没有」的 Boss 牌库；\n" +
                "· 本地已归档的牌库保持原样，不会被云端覆盖；\n" +
                "· 云端未归档的草稿不会写入。\n\n继续？",
                "合并网络源到本地", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _busy = true;
        UpdateButtons();
        StatusText.Text = $"正在加载「{source.Name}」…";

        string message;
        try
        {
            message = await _viewModel.MergeSourceIntoLocalAsync(source.Id);
        }
        catch (Exception ex)
        {
            message = $"合并失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }

        StatusText.Text = message;
        LoadRows();

        System.Windows.MessageBox.Show(message, "合并网络源到本地",
            MessageBoxButton.OK,
            message.Contains("失败") ? MessageBoxImage.Warning : MessageBoxImage.Information);

        PromptSwitchToLocal();
    }

    /// <summary>写入的是本地录制：当前显示网络源时提示切换，否则看不到刚导入的数据。</summary>
    private async void PromptSwitchToLocal()
    {
        if (_viewModel.IsLocalSource) return;

        var answer = System.Windows.MessageBox.Show(
            $"数据已写入本地录制。当前显示的是只读网络源「{_viewModel.ActiveSourceName}」，" +
            "是否切换到「本地录制」查看？",
            "牌库数据源", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _busy = true;
        UpdateButtons();
        try
        {
            StatusText.Text = await _viewModel.SetActiveSourceAsync(DataSourceConfig.LocalId);
            LoadRows();
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
