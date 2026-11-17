using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LoRPoCTracker.Config;
using LoRPoCTracker.Models;
using LoRPoCTracker.Services;
using LoRPoCTracker.ViewModels;

namespace LoRPoCTracker.Views;

public partial class SourceManagerWindow : Window
{
    private readonly AppConfig _config;
    private readonly OverlayViewModel _viewModel;
    private readonly ObservableCollection<DataSourceConfig> _sources;

    public SourceManagerWindow(AppConfig config, OverlayViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);

        _config = config;
        _viewModel = viewModel;
        _sources = new ObservableCollection<DataSourceConfig>(config.Sources);
        SourceList.ItemsSource = _sources;

        var active = _sources.FirstOrDefault(source => source.Id == config.ActiveSourceId);
        if (active is not null) SourceList.SelectedItem = active;
    }

    private DataSourceConfig? Selected => SourceList.SelectedItem as DataSourceConfig;

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var source = Selected;
        var isLocal = source?.IsLocal ?? true;

        NameBox.Text = source?.Name ?? "";
        FilesBox.Text = source is null
            ? ""
            : string.Join(Environment.NewLine, source.Files ?? new List<string>());
        NameBox.IsEnabled = source is not null && !isLocal;
        FilesBox.IsEnabled = source is not null && !isLocal;

        HintText.Text = source is null
            ? "从左侧选择数据源；点「添加网络源」可新增任意只读网络源。"
            : isLocal
                ? "本地源固定为 data/encounters.json，不需要添加或配置，也是管理员录制、编辑、定档的唯一写入目标。"
                : "网络源只读：切换到它之后，自动录制与牌库编辑会暂停；请切回本地源再维护数据。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var source = Selected;
        if (source is null || source.IsLocal)
        {
            System.Windows.MessageBox.Show("本地源无需配置，请先选择一个网络源。", "牌库数据源",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = FilesBox.Text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var invalid = lines.FirstOrDefault(line =>
            !Uri.TryCreate(line, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps));
        if (invalid is not null)
        {
            System.Windows.MessageBox.Show($"文件地址必须是 http/https 直链：\n{invalid}", "牌库数据源",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = NameBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(name)) source.Name = name;
        source.Files = lines;
        _config.Save();
        SourceList.Items.Refresh();

        System.Windows.MessageBox.Show("已保存。若这是当前源，请点「清缓存并刷新」重新加载。", "牌库数据源",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var source = new DataSourceConfig
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = $"网络源 {_sources.Count}",
            IsLocal = false,
            Files = new List<string>()
        };

        _config.Sources.Add(source);
        _sources.Add(source);
        _config.Save();
        SourceList.SelectedItem = source;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var source = Selected;
        if (source is null || source.IsLocal)
        {
            System.Windows.MessageBox.Show("本地源不可删除，只能删除网络源。", "牌库数据源",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (System.Windows.MessageBox.Show($"确定删除数据源「{source.Name}」？", "牌库数据源",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _sources.Remove(source);
        _config.Sources.Remove(source);
        EncounterSourceLoader.ClearCache(source.Id);

        if (_config.ActiveSourceId == source.Id)
        {
            _config.ActiveSourceId = DataSourceConfig.LocalId;
            _ = _viewModel.SetActiveSourceAsync(DataSourceConfig.LocalId);
        }

        _config.Save();
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        var source = Selected;
        if (source is null) return;

        var message = await _viewModel.SetActiveSourceAsync(source.Id, forceRefresh: true);
        SourceList.Items.Refresh();
        System.Windows.MessageBox.Show($"已切换到「{source.Name}」\n{message}", "牌库数据源",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var source = Selected;
        if (source is null) return;

        if (source.IsLocal)
        {
            System.Windows.MessageBox.Show("本地源无需刷新。", "牌库数据源",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EncounterSourceLoader.ClearCache(source.Id);
        var message = await _viewModel.SetActiveSourceAsync(source.Id, forceRefresh: true);
        System.Windows.MessageBox.Show(message, "牌库数据源",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
