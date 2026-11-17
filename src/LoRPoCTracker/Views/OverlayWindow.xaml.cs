using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LoRPoCTracker.Config;
using LoRPoCTracker.Models;
using LoRPoCTracker.Services;
using LoRPoCTracker.ViewModels;
using WinForms = System.Windows.Forms;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;

namespace LoRPoCTracker.Views;

public partial class OverlayWindow : Window
{
    private const int HotkeyClickThrough = 0xA001;
    private const int HotkeyReset = 0xA002;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly AppConfig _config;
    private readonly CardDatabase _cards;
    private readonly LocalizationDatabase _loc;
    private readonly GameDataIndex _gameData;
    private readonly OverlayViewModel _viewModel;
    private readonly DispatcherTimer _topmostTimer;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripItem? _trayResetItem;
    private bool _clickThrough;
    private bool _menuOpen;
    private bool _tooltipOpen;
    private bool _modalOpen;
    private bool _exitConfirmed;
    private bool _assetsBusy;
    private bool _updateChecking;
    private bool _dragging;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private BoolKey _lastDockKey;

    public OverlayWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();
        _cards = new CardDatabase(_config.Locale, _config.GameDir);
        _loc = new LocalizationDatabase(_config.Locale, _config.GameDir);
        _gameData = new GameDataIndex(_config.GameDir);
        _viewModel = new OverlayViewModel(_config, _cards, _loc, _gameData);
        DataContext = _viewModel;

        Width = _config.Width;
        Height = _config.Height;
        ApplyBackground();
        UpdateVersionBadge();
        ThemeManager.ThemeChanged += OnThemeChanged;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler(OnToolTipOpening));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipClosingEvent,
            new ToolTipEventHandler(OnToolTipClosing));
        ApplyInitialPosition();

        Loaded += OnLoaded;
        Closed += OnClosed;

        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            if (IsVisible && !_menuOpen && !_tooltipOpen && !_modalOpen) WindowEffects.ReassertTopmost(this);
            if (_config.AutoDock) TryFollowGameWindow();
        };
        _topmostTimer.Start();

        CreateTrayIcon();
    }

    private readonly record struct BoolKey(int Left, int Top, int Width, int Height);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowEffects.ApplyOverlayStyles(this);
        SetClickThrough(_config.ClickThrough);

        await LoadCardsAsync(forceRefresh: false);
        _viewModel.Start();

        // 载入配置的数据源（本地源或只读网络源；失败会自动回退本地）
        var sourceMessage = await _viewModel.ApplyActiveSourceAsync();
        if (sourceMessage.Contains("失败"))
        {
            System.Windows.MessageBox.Show(sourceMessage, "牌库数据源",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 默认在后台把当前版本的 PvE 资源包下载到本地并整合全部卡牌数据
        _ = EnsureCardAssetsAsync(force: false, allStandardImages: false);

        // 启动后台静默检查更新（不打断当前操作）
        if (_config.CheckUpdateOnStartup) _ = CheckUpdateAsync(silent: true);
    }

    /// <summary>后台检查更新；silent=true 时只在发现新版本才弹窗。</summary>
    private async Task CheckUpdateAsync(bool silent)
    {
        if (_updateChecking) return;
        _updateChecking = true;
        try
        {
            var service = new UpdateService();
            var (info, _, error) = await service.CheckAsync().ConfigureAwait(true);
            if (info is null)
            {
                if (!silent)
                    System.Windows.MessageBox.Show(
                        "检查更新失败：" + error + "\n\n可稍后重试，或到项目 Release 页面手动下载。",
                        "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!UpdateService.IsNewer(info))
            {
                if (!silent)
                    System.Windows.MessageBox.Show(
                        $"当前已是最新版本（v{UpdateService.CurrentVersion}）。",
                        "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (silent && string.Equals(_config.IgnoredUpdateVersion, info.Version, StringComparison.Ordinal))
                return;

            var window = new UpdateWindow(info, UpdateService.CurrentVersion, silent);
            _modalOpen = true;
            try { window.ShowDialog(); }
            finally { _modalOpen = false; }
            if (silent && window.IsUpdateDismissed)
            {
                _config.IgnoredUpdateVersion = info.Version;
                _config.Save();
            }
        }
        catch (Exception ex)
        {
            if (!silent)
                System.Windows.MessageBox.Show("检查更新出错：" + ex.Message, "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateChecking = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_exitConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        // 等 Closing 事件返回后再弹确认框，避免在关闭流程里重入 Close()
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (TryConfirmExit()) ExitApplication();
        }));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _viewModel.Dispose();
        _topmostTimer.Stop();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        if (_source is not null && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyClickThrough);
            UnregisterHotKey(_hwnd, HotkeyReset);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        RegisterHotKey(hwnd, HotkeyClickThrough, ModControl | ModShift, 0x4F); // Ctrl+Shift+O
        RegisterHotKey(hwnd, HotkeyReset, ModControl | ModShift, 0x52);        // Ctrl+Shift+R
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyClickThrough:
                    SetClickThrough(!_clickThrough);
                    handled = true;
                    break;
                case HotkeyReset:
                    // 重置记录属于管理员版功能
                    if (_config.AdminMode) _viewModel.ResetMatch();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ApplyBackground()
    {
        var alpha = (byte)Math.Round(Math.Clamp(_config.Opacity, 0.3, 1.0) * 255);
        var color = TryFindResource("OverlaySurfaceColor") is Color themed ? themed : Color.FromRgb(0x12, 0x15, 0x1B);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        RootBorder.Background = brush;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyBackground();

    /// <summary>ToolTip 是独立弹窗，打开期间暂停主窗口的置顶刷新，避免提示被记牌器盖住。</summary>
    private void OnToolTipOpening(object sender, ToolTipEventArgs e) => _tooltipOpen = true;

    private void OnToolTipClosing(object sender, ToolTipEventArgs e) => _tooltipOpen = false;

    private void CardToolTip_Opened(object sender, RoutedEventArgs e) => _tooltipOpen = true;

    private void CardToolTip_Closed(object sender, RoutedEventArgs e) => _tooltipOpen = false;

    /// <summary>悬停卡牌行时按需下载并显示卡牌图片。</summary>
    private async void CardRow_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (!_config.ShowCardImages) return;
        if (sender is not FrameworkElement { DataContext: CardRow row }) return;
        if (row.ImageSource is not null || row.ImageRequested) return;

        row.ImageRequested = true;
        var image = await _viewModel.LoadCardImageAsync(row.Code);
        if (image is not null) row.ImageSource = image;
        else row.ImageRequested = false; // 失败时允许下次悬停重试
    }

    /// <summary>右键「对手已打出」的卡牌：标记为衍生，之后不再计入牌库录制。</summary>
    private void OpponentRows_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 只有录制模式（管理员版 + 本地源）才允许标记衍生：会写入录制数据
        if (!_viewModel.CanWriteEncounters) return;
        if (FindCardRow(e.OriginalSource as DependencyObject) is not { } row) return;

        var menu = new ContextMenu();
        var mark = new MenuItem { Header = "标记为衍生（不计入录制）" };
        mark.Click += (_, _) =>
        {
            _viewModel.MarkOpponentToken(row.Code);
            row.IsToken = true;
            row.IsNew = false;
        };
        menu.Items.Add(mark);
        menu.PlacementTarget = e.OriginalSource as UIElement;
        menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// 右键「Boss 牌库」的卡牌：标记「已去除」——对手丢弃/移出牌库时接口没有记录，
    /// 手动从剩余牌库扣 1 张（只影响本局显示，不写入录制）。仅录制模式可用。
    /// </summary>
    private void BossRows_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.CanWriteEncounters) return;
        if (FindCardRow(e.OriginalSource as DependencyObject) is not { } row) return;

        var menu = new ContextMenu();

        var remove = new MenuItem { Header = "已去除（-1）" };
        remove.Click += (_, _) => _viewModel.RemoveBossCard(row.Code);
        menu.Items.Add(remove);

        var removed = _viewModel.ManualRemovedCount(row.Code);
        if (removed > 0)
        {
            var undo = new MenuItem { Header = $"撤销已去除（+1，已去除 {removed} 张）" };
            undo.Click += (_, _) => _viewModel.UndoRemoveBossCard(row.Code);
            menu.Items.Add(undo);
        }
        menu.PlacementTarget = e.OriginalSource as UIElement;
        menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>点击「本地图 Boss 牌库」中的一行：展开 / 收起该 Boss 的牌库明细。</summary>
    private void MapBossRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindMapBossRow(e.OriginalSource as DependencyObject) is not { } row) return;
        row.IsExpanded = !row.IsExpanded;
        e.Handled = true;
    }

    /// <summary>右键「本地图 Boss 牌库」：编辑该 Boss 牌库 / 清除该 Boss 录制（管理员版）。</summary>
    private void MapBossRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindMapBossRow(e.OriginalSource as DependencyObject) is not { } row) return;
        if (!_viewModel.CanWriteEncounters) return;

        var menu = new ContextMenu();

        var edit = new MenuItem { Header = "编辑该 Boss 牌库…" };
        edit.Click += (_, _) => ShowModal(new CardEditorWindow(_viewModel, row.Key));
        menu.Items.Add(edit);

        var clear = new MenuItem { Header = "清除该 Boss 录制" };
        clear.Click += (_, _) =>
        {
            _modalOpen = true;
            MessageBoxResult answer;
            try
            {
                answer = System.Windows.MessageBox.Show(
                    $"确定清除「{row.Name}」的牌库录制？\n\n该操作只影响这个关卡的录制数据。",
                    "清除录制", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            }
            finally
            {
                _modalOpen = false;
            }

            if (answer != MessageBoxResult.OK) return;
            _viewModel.DeleteEncounter(row.Key);
        };
        menu.Items.Add(clear);

        menu.PlacementTarget = e.OriginalSource as UIElement;
        menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MapBossRow? FindMapBossRow(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: MapBossRow row }) return row;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private static CardRow? FindCardRow(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: CardRow row }) return row;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void ApplyInitialPosition()
    {
        if (_config.Left is { } left && _config.Top is { } top)
        {
            Left = left;
            Top = top;
        }
        else if (_config.AutoDock && TryDock())
        {
            // 已吸附
        }
        else
        {
            Left = 40;
            Top = 120;
        }

        ClampToScreen();
    }

    private bool TryDock()
    {
        var game = GameWindowLocator.Find();
        if (game is null) return false;

        var position = GameWindowLocator.GetDockPosition(game.Value, Width, Height);
        if (position is null) return false;

        Left = position.Value.Left;
        Top = position.Value.Top;
        _lastDockKey = new BoolKey(game.Value.Bounds.Left, game.Value.Bounds.Top,
            game.Value.Bounds.Width, game.Value.Bounds.Height);
        return true;
    }

    /// <summary>游戏窗口移动/改变大小后重新贴边。</summary>
    private void TryFollowGameWindow()
    {
        var game = GameWindowLocator.Find();
        if (game is null) return;

        var key = new BoolKey(game.Value.Bounds.Left, game.Value.Bounds.Top,
            game.Value.Bounds.Width, game.Value.Bounds.Height);
        if (key == _lastDockKey) return;

        var position = GameWindowLocator.GetDockPosition(game.Value, Width, Height);
        if (position is null) return;

        Left = position.Value.Left;
        Top = position.Value.Top;
        _lastDockKey = key;
    }

    private void ClampToScreen()
    {
        var minLeft = SystemParameters.VirtualScreenLeft;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxLeft = minLeft + SystemParameters.VirtualScreenWidth - Width;
        var maxTop = minTop + SystemParameters.VirtualScreenHeight - Height;

        if (maxLeft > minLeft) Left = Math.Clamp(Left, minLeft, maxLeft);
        if (maxTop > minTop) Top = Math.Clamp(Top, minTop, maxTop);
    }

    private void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        WindowEffects.SetClickThrough(this, enabled);
        ThroughBadge.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = enabled
            ? "穿透开启：Ctrl+Shift+O 关闭 · 托盘图标可操作"
            : "Ctrl+Shift+O 穿透 · 拖动标题栏移动";

        _config.ClickThrough = enabled;
        _config.Save();
    }

    /// <summary>
    /// 默认后台任务：确保当前版本的卡牌资源已就绪，合并卡牌数据/卡图，
    /// 补齐描述并补拼剩余游戏独占卡。force 重新下载，allStandardImages 批量下标准卡图。
    /// </summary>
    private async Task EnsureCardAssetsAsync(bool force, bool allStandardImages)
    {
        if (_assetsBusy) return;
        _assetsBusy = true;

        IProgress<string> progress = new Progress<string>(text =>
            _viewModel.SetStatus(text, "卡牌资源导入中，记牌器可继续使用", true));

        try
        {
            var importer = new CardAssetImporter(_cards, _loc);
            var report = await Task.Run(() =>
                importer.EnsureAsync(force, allStandardImages, progress, CancellationToken.None));

            // 99 系列：首次启动从游戏资源生成原画/卡框元素，然后重拼全部 99 卡图
            progress.Report("正在加载 99 系列卡图资源…");
            var (gameAssets, composed99) = await _viewModel.Ensure99CardAssetsAsync(progress, force);

            progress.Report("正在补拼剩余游戏独占卡…");
            var composed = await _viewModel.ComposeMissingCardsAsync(progress);

            var finalImages = Directory.EnumerateFiles(
                Path.Combine(AppPaths.DataDir, "images", _config.Locale), "*.png").Count();
            await _viewModel.ReloadImagesAsync();
            _viewModel.ForceRefresh();
            _viewModel.SetStatus($"卡牌资源 {report.Version}",
                $"共 {report.TotalCards} 张可预览：本地卡图 {finalImages} 张、" +
                $"官方图按需下载 {report.NetworkImageCards} 张、99 补拼 {composed99} 张" +
                (gameAssets.Extracted ? $"、提取原画 {gameAssets.Illustrations} 张" : ""), false);
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus("卡牌资源导入失败", ex.Message, false);
        }
        finally
        {
            _assetsBusy = false;
        }
    }

    private async Task LoadCardsAsync(bool forceRefresh)
    {
        if (forceRefresh)
        {
            try
            {
                File.Delete(AppPaths.CardCachePath(_config.Locale));
            }
            catch
            {
                // 缓存被占用时忽略
            }
        }

        _viewModel.SetStatus("正在加载卡牌数据…", "首次启动需要从 Data Dragon 下载，请稍候", isBusy: true);

        try
        {
            await Task.Run(() => _cards.LoadAsync());
            await Task.Run(() => _loc.LoadAsync());
            await Task.Run(() => _gameData.LoadAsync());
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus("卡牌数据加载失败", ex.Message, isBusy: false);
            HintText.Text = "卡牌数据不可用，将显示卡牌代码";
            return;
        }

        _viewModel.ForceRefresh();
        _viewModel.PruneGeneratedCards();

        if (!_cards.IsLoaded)
        {
            HintText.Text = "卡牌数据未加载，将显示卡牌代码（菜单可重试）";
        }
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _dragging = true;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        HeaderBar.CaptureMouse();
        e.Handled = true;
    }

    private void HeaderBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var now = PointToScreen(e.GetPosition(this));
        var scale = WindowEffects.GetDpiScale(WindowEffects.GetHandle(this));
        if (scale <= 0) scale = 1;

        Left = _dragStartLeft + (now.X - _dragStartScreen.X) / scale;
        Top = _dragStartTop + (now.Y - _dragStartScreen.Y) / scale;
    }

    private void HeaderBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        HeaderBar.ReleaseMouseCapture();
        _config.AutoDock = false;
        SavePosition();
    }

    private void SavePosition()
    {
        _config.Left = Left;
        _config.Top = Top;
        _config.Width = Width;
        _config.Height = Height;
        _config.Save();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_config.AdminMode) return;
        _viewModel.ResetMatch();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestExit();

    /// <summary>最小化到系统托盘图标。</summary>
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SavePosition();
        Hide();
    }

    /// <summary>退出前确认；勾选「下次不再提示」后写入配置，后续直接退出。</summary>
    private bool TryConfirmExit()
    {
        if (!_config.ConfirmExit) return true;

        var dialog = new ExitConfirmWindow();
        // 托盘退出时主窗可能已隐藏，隐藏窗口作 Owner 会导致对话框不显示
        if (IsVisible) dialog.Owner = this;
        if (!ShowModal(dialog)) return false;

        if (dialog.DontAskAgain)
        {
            _config.ConfirmExit = false;
            _config.Save();
        }

        return true;
    }

    private void RequestExit()
    {
        if (_exitConfirmed) return;
        if (!TryConfirmExit()) return;

        ExitApplication();
    }

    private void ExitApplication()
    {
        if (_exitConfirmed) return;

        _exitConfirmed = true;
        SavePosition();
        Close();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildMenu();
        // 菜单开在窗口外侧：优先右侧，右侧放不下时放到左侧，避免和记牌器重叠抢置顶
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = PlaceMenu;
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
    }

    /// <summary>优先贴在窗口右侧；右侧空间不足时贴到窗口左侧。</summary>
    private CustomPopupPlacement[] PlaceMenu(System.Windows.Size popupSize, System.Windows.Size targetSize, Point offset)
    {
        const double gap = 8;
        var workArea = GetWorkArea();

        var fitsRight = Left + Width + gap + popupSize.Width <= workArea.Right;
        var x = fitsRight ? targetSize.Width + gap : -popupSize.Width - gap;

        return new[] { new CustomPopupPlacement(new Point(x, 0), PopupPrimaryAxis.Vertical) };
    }

    /// <summary>当前窗口所在屏幕的工作区（DIP）。</summary>
    private Rect GetWorkArea()
    {
        var handle = WindowEffects.GetHandle(this);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var area = WinForms.Screen.FromHandle(handle).WorkingArea;
                var scale = WindowEffects.GetDpiScale(handle);
                if (scale <= 0) scale = 1;
                return new Rect(area.Left / scale, area.Top / scale, area.Width / scale, area.Height / scale);
            }
            catch
            {
                // 退回主屏工作区
            }
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>打开模态窗口；期间暂停主窗口置顶刷新，避免盖住对话框抢走点击。</summary>
    private bool ShowModal(Window window)
    {
        _modalOpen = true;
        try
        {
            return window.ShowDialog() == true;
        }
        finally
        {
            _modalOpen = false;
        }
    }

    /// <summary>切换普通版 / 管理员版；开启管理员版时先给出功能与定档说明提示。</summary>
    private void SwitchAdminMode(bool enable)
    {
        if (_config.AdminMode == enable) return;

        if (enable)
        {
            MessageBoxResult answer;
            _modalOpen = true;
            try
            {
                answer = System.Windows.MessageBox.Show(
                    "即将开启管理员版：\n\n" +
                    "· 菜单会显示牌库录制、编辑、管理与重置记录选项，战斗结束后自动写入录制数据；\n" +
                    "· 牌库整理完成后，请在「管理关卡录制 / 重命名…」中执行「定档牌库」；\n" +
                    "· 定档后该关卡不再自动录制，但牌库仍可手动编辑。\n\n" +
                    "普通版不显示这些选项，也不会写入任何牌库录制数据。\n确定开启管理员版？",
                    "开启管理员版", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            }
            finally
            {
                _modalOpen = false;
            }

            if (answer != MessageBoxResult.OK) return;
        }

        _config.AdminMode = enable;
        _config.Save();
        UpdateVersionBadge();
        _viewModel.ForceRefresh();
    }

    /// <summary>
    /// 普通版：标题栏不显示版本标记，重置相关入口全部隐藏；
    /// 切换到管理员版：显示「录制模式」标记并放出重置入口。
    /// </summary>
    private void UpdateVersionBadge()
    {
        var admin = _config.AdminMode;
        var visibility = admin ? Visibility.Visible : Visibility.Collapsed;

        VersionBadgeBox.Visibility = visibility;
        VersionBadge.Text = "录制模式";
        ResetButton.Visibility = visibility;
        if (_trayResetItem is not null) _trayResetItem.Available = admin;

        HintText.Text = admin
            ? "Ctrl+Shift+O 穿透 · 录制模式：牌库完善后请在管理窗口定档"
            : "Ctrl+Shift+O 穿透 · 拖动标题栏移动";
    }

    /// <summary>数据源切换结果提示（显示期间暂停置顶刷新，避免被悬浮窗盖住）。</summary>
    private void ShowSourceMessage(string message)
    {
        _modalOpen = true;
        try
        {
            System.Windows.MessageBox.Show(message, "牌库数据源", MessageBoxButton.OK,
                message.Contains("失败") ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        finally
        {
            _modalOpen = false;
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // 两个版本在这里切换；普通版不生成任何录制相关选项
        var versionMenu = new MenuItem
        {
            Header = $"软件版本（当前：{(_config.AdminMode ? "管理员版" : "普通版")}）"
        };
        var normalMode = new MenuItem
        {
            Header = "普通版（仅记牌）",
            IsCheckable = true,
            IsChecked = !_config.AdminMode
        };
        normalMode.Click += (_, _) => SwitchAdminMode(false);
        versionMenu.Items.Add(normalMode);

        var adminMode = new MenuItem
        {
            Header = "管理员版（录制 / 编辑牌库）",
            IsCheckable = true,
            IsChecked = _config.AdminMode
        };
        adminMode.Click += (_, _) => SwitchAdminMode(true);
        versionMenu.Items.Add(adminMode);

        menu.Items.Add(versionMenu);

        // 数据源：所有版本都能选择；网络源只读，可自行添加 / 配置多个文件
        var sourceMenu = new MenuItem { Header = $"牌库数据源（当前：{_viewModel.ActiveSourceName}）" };
        foreach (var source in _config.Sources)
        {
            var item = new MenuItem
            {
                Header = $"{source.Name}（{source.Summary}）",
                IsCheckable = true,
                IsChecked = source.Id == _config.ActiveSourceId
            };
            var captured = source.Id;
            item.Click += async (_, _) =>
            {
                var message = await _viewModel.SetActiveSourceAsync(captured, forceRefresh: true);
                ShowSourceMessage(message);
            };
            sourceMenu.Items.Add(item);
        }

        var refreshSource = new MenuItem
        {
            Header = "刷新当前网络源",
            IsEnabled = !_viewModel.IsLocalSource
        };
        refreshSource.Click += async (_, _) =>
        {
            var message = await _viewModel.SetActiveSourceAsync(_config.ActiveSourceId, forceRefresh: true);
            ShowSourceMessage(message);
        };
        sourceMenu.Items.Add(new Separator());
        sourceMenu.Items.Add(refreshSource);

        var manageSource = new MenuItem { Header = "管理 / 添加网络源…" };
        manageSource.Click += (_, _) => ShowModal(new SourceManagerWindow(_config, _viewModel));
        sourceMenu.Items.Add(manageSource);

        menu.Items.Add(sourceMenu);

        // 单 Boss 牌库导出 / 导入：普通版也能导入，导出在窗口内按录制模式 + 归档状态判断
        var deckTransfer = new MenuItem { Header = "Boss 牌库导出 / 导入 / 云源合并…" };
        deckTransfer.Click += (_, _) => ShowModal(new DeckTransferWindow(_viewModel));
        menu.Items.Add(deckTransfer);

        menu.Items.Add(new Separator());

        var clickThrough = new MenuItem { Header = "点击穿透（Ctrl+Shift+O）", IsCheckable = true, IsChecked = _clickThrough };
        clickThrough.Click += (_, _) => SetClickThrough(!_clickThrough);
        menu.Items.Add(clickThrough);

        var hideExhausted = new MenuItem
        {
            Header = "隐藏已用尽的牌",
            IsCheckable = true,
            IsChecked = _config.HideExhausted
        };
        hideExhausted.Click += (_, _) =>
        {
            _config.HideExhausted = hideExhausted.IsChecked;
            _config.Save();
            _viewModel.ForceRefresh();
        };
        menu.Items.Add(hideExhausted);

        var showImages = new MenuItem
        {
            Header = "显示卡牌图片（悬停/编辑，本地）",
            IsCheckable = true,
            IsChecked = _config.ShowCardImages
        };
        showImages.Click += (_, _) =>
        {
            _config.ShowCardImages = showImages.IsChecked;
            _config.Save();
            _viewModel.ForceRefresh();
        };
        menu.Items.Add(showImages);

        var showMapBoss = new MenuItem
        {
            Header = "显示本地图 Boss 牌库（进地图时）",
            IsCheckable = true,
            IsChecked = _config.ShowMapBossDecks
        };
        showMapBoss.Click += (_, _) =>
        {
            _config.ShowMapBossDecks = showMapBoss.IsChecked;
            _config.Save();
            _viewModel.ForceRefresh();
        };
        menu.Items.Add(showMapBoss);

        var showDefeatedBosses = new MenuItem
        {
            Header = "显示本局已打过的 Boss",
            IsCheckable = true,
            IsChecked = !_config.HideDefeatedMapBosses
        };
        showDefeatedBosses.Click += (_, _) =>
        {
            _config.HideDefeatedMapBosses = !showDefeatedBosses.IsChecked;
            _config.Save();
            _viewModel.ForceRefresh();
        };
        menu.Items.Add(showDefeatedBosses);

        var autoDock = new MenuItem { Header = "自动贴边到游戏窗口", IsCheckable = true, IsChecked = _config.AutoDock };
        autoDock.Click += (_, _) =>
        {
            _config.AutoDock = autoDock.IsChecked;
            _config.Save();
            if (_config.AutoDock) TryDock();
        };
        menu.Items.Add(autoDock);

        if (_viewModel.CanWriteEncounters)
        {
            var recordBoss = new MenuItem
            {
                Header = "自动录制 Boss 牌库",
                IsCheckable = true,
                IsChecked = _config.RecordBossDecks
            };
            recordBoss.Click += (_, _) =>
            {
                _config.RecordBossDecks = recordBoss.IsChecked;
                _config.Save();
                _viewModel.ForceRefresh();
            };
            menu.Items.Add(recordBoss);
        }

        var opacityMenu = new MenuItem { Header = "透明度" };
        foreach (var value in new[] { 1.0, 0.9, 0.75, 0.6 })
        {
            var item = new MenuItem
            {
                Header = $"{value * 100:0}%",
                IsCheckable = true,
                IsChecked = Math.Abs(_config.Opacity - value) < 0.01
            };
            var captured = value;
            item.Click += (_, _) =>
            {
                _config.Opacity = captured;
                _config.Save();
                ApplyBackground();
            };
            opacityMenu.Items.Add(item);
        }

        menu.Items.Add(opacityMenu);
        menu.Items.Add(new Separator());

        // 重置记录属于管理员版功能，普通版不显示
        if (_config.AdminMode)
        {
            var reset = new MenuItem { Header = "重置本局记录（Ctrl+Shift+R）" };
            reset.Click += (_, _) => _viewModel.ResetMatch();
            menu.Items.Add(reset);
        }

        // 录制 / 编辑 / 管理相关选项：普通版或只读网络源下完全不显示
        if (_viewModel.CanWriteEncounters)
        {
            var editDeck = new MenuItem
            {
                Header = "编辑当前 Boss 牌库…",
                IsEnabled = _viewModel.HasCurrentEncounter
            };
            editDeck.Click += (_, _) =>
            {
                ShowModal(new CardEditorWindow(_viewModel));
            };
            menu.Items.Add(editDeck);

            var manage = new MenuItem { Header = "管理关卡录制 / 重命名…" };
            manage.Click += (_, _) =>
            {
                ShowModal(new EncounterEditorWindow(_viewModel));
            };
            menu.Items.Add(manage);

            var clearBoss = new MenuItem
            {
                Header = "清除当前 Boss 录制",
                IsEnabled = _viewModel.HasCurrentEncounter
            };
            clearBoss.Click += (_, _) => _viewModel.ClearCurrentBossRecord();
            menu.Items.Add(clearBoss);

            var openData = new MenuItem { Header = "打开录制数据文件夹" };
            openData.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = AppPaths.DataDir,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // 忽略资源管理器启动失败
                }
            };
            menu.Items.Add(openData);
        }
        else if (!_viewModel.IsLocalSource)
        {
            // 只读网络源：说明为什么录制 / 编辑选项不显示
            menu.Items.Add(new MenuItem
            {
                Header = $"只读网络源「{_viewModel.ActiveSourceName}」· 录制/编辑已暂停",
                IsEnabled = false
            });
        }

        var update = new MenuItem { Header = $"检查更新（当前 v{UpdateService.CurrentVersion}）" };
        update.Click += async (_, _) => await CheckUpdateAsync(silent: false);
        menu.Items.Add(update);

        var autoCheck = new MenuItem
        {
            Header = "启动时自动检查更新",
            IsCheckable = true,
            IsChecked = _config.CheckUpdateOnStartup
        };
        autoCheck.Click += (_, _) =>
        {
            _config.CheckUpdateOnStartup = autoCheck.IsChecked;
            _config.Save();
        };
        menu.Items.Add(autoCheck);

        menu.Items.Add(new Separator());

        var reload = new MenuItem { Header = "重新下载卡牌数据" };
        reload.Click += async (_, _) => await LoadCardsAsync(forceRefresh: true);
        menu.Items.Add(reload);

        var updateAssets = new MenuItem { Header = "更新当前版本卡牌资源包…" };
        updateAssets.Click += async (_, _) =>
            await EnsureCardAssetsAsync(force: true, allStandardImages: false);
        menu.Items.Add(updateAssets);

        var offlineImages = new MenuItem { Header = "下载全部官方卡图（离线预览）…" };
        offlineImages.Click += async (_, _) =>
            await EnsureCardAssetsAsync(force: false, allStandardImages: true);
        menu.Items.Add(offlineImages);

        var status = new MenuItem { Header = "日志 / 本地化状态" };
        status.Click += (_, _) => System.Windows.MessageBox.Show(
            _viewModel.BuildDiagnostics(), "运行状态", MessageBoxButton.OK, MessageBoxImage.Information);
        menu.Items.Add(status);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => RequestExit();
        menu.Items.Add(exit);

        return menu;
    }

    private void CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();

        menu.Items.Add("显示 / 隐藏", null, (_, _) =>
        {
            if (IsVisible) Hide();
            else
            {
                Show();
                WindowEffects.ReassertTopmost(this);
            }
        });

        var throughItem = new WinForms.ToolStripMenuItem("点击穿透") { CheckOnClick = true };
        throughItem.Click += (_, _) => SetClickThrough(throughItem.Checked);
        menu.Items.Add(throughItem);

        _trayResetItem = menu.Items.Add("重置本局", null, (_, _) => _viewModel.ResetMatch());
        _trayResetItem.Available = _config.AdminMode;
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(new Action(RequestExit)));

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "LoR 英雄之路记牌器",
            Visible = true,
            ContextMenuStrip = menu
        };

        _tray.DoubleClick += (_, _) =>
        {
            if (IsVisible) Hide();
            else
            {
                Show();
                WindowEffects.ReassertTopmost(this);
            }
        };
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "icons", "app.ico");
        return File.Exists(path) ? new System.Drawing.Icon(path) : System.Drawing.SystemIcons.Application;
    }
}
