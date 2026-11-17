using System.Diagnostics;
using System.IO;
using System.Windows;
using LoRPoCTracker.Models;
using LoRPoCTracker.Services;
using MessageBox = System.Windows.MessageBox;

namespace LoRPoCTracker.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly UpdateService _service = new();
    private bool _busy;

    /// <summary>用户点了「稍后」（而非下载或关闭），用于静默检查时记住忽略版本。</summary>
    public bool IsUpdateDismissed { get; private set; }

    public UpdateWindow(UpdateInfo info, string currentVersion, bool silentCheck = false)
    {
        InitializeComponent();
        _info = info;

        TitleText.Text = silentCheck ? "发现新版本" : "软件更新";
        SubText.Text = $"当前版本 v{currentVersion}  →  新版本 v{info.Version}" +
                       (string.IsNullOrWhiteSpace(info.Published) ? "" : $"\n发布于 {info.Published}");
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "（本次更新未提供说明）" : info.Notes.Trim();
        StatusText.Text = info.Size <= 0
            ? "点击「下载并安装」自动完成更新；网络受限时会尝试多个下载源。"
            : $"安装包大小约 {info.Size / 1024.0 / 1024.0:0.#} MB，下载后自动校验并安装。";
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) IsUpdateDismissed = true;
        Close();
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        var url = $"https://github.com/{UpdateService.RepoOwner}/{UpdateService.RepoName}/releases/latest";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show($"打开失败，请手动访问：\n{url}", "下载页", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        UpdateButton.IsEnabled = false;
        LaterButton.Content = "取消";

        var progress = new Progress<string>(text => StatusText.Text = text);
        try
        {
            var zip = await _service.DownloadAsync(_info, progress).ConfigureAwait(true);
            UpdateService.InstallAndRestart(zip);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消更新";
        }
        catch (Exception ex)
        {
            StatusText.Text = "更新失败：" + ex.Message;
            MessageBox.Show("更新失败：\n" + ex.Message, "软件更新", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            UpdateButton.IsEnabled = true;
            LaterButton.Content = "稍后";
        }
    }
}
