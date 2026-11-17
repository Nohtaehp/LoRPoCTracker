using System.IO;
using System.Windows;
using LoRPoCTracker.Config;
using LoRPoCTracker.Services;
using LoRPoCTracker.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace LoRPoCTracker;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "startup.log"),
                DateTime.Now + " args=[" + string.Join("|", e.Args) + "]\n");
        }
        catch
        {
            // 忽略启动日志失败
        }

        // 在主窗口创建前根据 Windows 浅色/深色模式装载主题
        ThemeManager.Initialize();

        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "startup.log"), "theme ok\n");
        }
        catch
        {
        }

        // 命令行：导入当前版本卡牌资源包（预置数据 / 验证覆盖度），不启动主窗口
        if (e.Args.Any(arg => string.Equals(arg, "--import-assets", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            try
            {
                var config = AppConfig.Load();
                var cards = new CardDatabase(config.Locale, config.GameDir);
                var loc = new LocalizationDatabase(config.Locale, config.GameDir);
                await cards.LoadAsync();
                await loc.LoadAsync();

                var importer = new CardAssetImporter(cards, loc);
                IProgress<string> progress = new Progress<string>(text => Log("assets: " + text));
                var report = await importer.EnsureAsync(false, false, progress);

                var gameData = new GameDataIndex(config.GameDir);
                await gameData.LoadAsync();
                using var viewModel = new OverlayViewModel(config, cards, loc, gameData);
                var (gameAssets, composed99) = await viewModel.Ensure99CardAssetsAsync(progress);
                var composed = await viewModel.ComposeMissingCardsAsync(progress);
                var finalImages = Directory.EnumerateFiles(
                    Path.Combine(AppPaths.DataDir, "images", config.Locale), "*.png").Count();
                Log($"assets done: version={report.Version} pve={report.PveCards} " +
                    $"total={report.TotalCards} localImages={report.LocalImages} " +
                    $"composed={composed} composed99={composed99} " +
                    $"extracted99={(gameAssets.Extracted ? 1 : 0)} illustrations={gameAssets.Illustrations} " +
                    $"elements={gameAssets.Elements} factionIcons={gameAssets.FactionIcons} " +
                    $"finalImages={finalImages} " +
                    $"networkImages={report.NetworkImageCards} standardDownloaded={report.StandardImagesDownloaded}");
            }
            catch (Exception ex)
            {
                Log("import-assets error: " + ex);
            }

            Shutdown();
            return;
        }

        // 命令行：只解析 Data Dragon 版本（本机分支 + CDN 校验），写日志后退出
        if (e.Args.Any(arg => string.Equals(arg, "--show-version", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            try
            {
                var config = AppConfig.Load();
                var cards = new CardDatabase(config.Locale, config.GameDir);
                await cards.LoadAsync();
                Log($"Data Dragon 版本 {cards.Version} | {cards.DdragonRootUrl}");
                Log($"PvE 数据 {cards.TpocDataUrl}");
                Log($"PvE 整包 {cards.TpocBundleUrl}");
            }
            catch (Exception ex)
            {
                Log("show-version error: " + ex);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("记牌器发生错误：\n" + args.Exception.Message, "LoR 英雄之路记牌器",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 主窗口改为代码创建：命令行模式（--import-assets / --show-version）不会误开窗口
        MainWindow = new Views.OverlayWindow();
        MainWindow.Show();
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "startup.log"), message + "\n");
        }
        catch
        {
            // 忽略日志失败
        }
    }
}
