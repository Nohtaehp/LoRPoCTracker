using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LoRPoCTracker.Services;

/// <summary>
/// 跟随 Windows 浅色/深色模式：读取系统个性化设置，运行时切换主题字典，
/// 并让编辑窗口的标题栏也变成深色。
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    private static readonly List<WeakReference<Window>> TrackedWindows = new();
    private static bool _hooked;

    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? ThemeChanged;

    public static void Initialize()
    {
        ApplyTheme(ReadSystemDark(), force: true);

        if (_hooked) return;
        _hooked = true;

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch
        {
            _hooked = false;
        }
    }

    /// <summary>让普通窗口（带系统标题栏）跟随主题，包括标题栏深色。</summary>
    public static void RegisterWindow(Window window)
    {
        TrackedWindows.RemoveAll(reference => !reference.TryGetTarget(out _));
        TrackedWindows.Add(new WeakReference<Window>(window));

        // InitializeComponent 之后窗口句柄可能还没创建，等 SourceInitialized / Loaded 再设置标题栏
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) ApplyWindowChrome(window);
        else window.SourceInitialized += (_, _) => ApplyWindowChrome(window);

        // 部分系统上需要窗口显示后再设置一次深色标题栏才生效
        window.Loaded += (_, _) => ApplyWindowChrome(window);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;

        var dark = ReadSystemDark();
        if (dark == IsDark) return;

        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(() => ApplyTheme(dark, force: false));
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            // AppsUseLightTheme：1 = 浅色，0 = 深色
            return key?.GetValue("AppsUseLightTheme") is int value ? value == 0 : false;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyTheme(bool dark, bool force)
    {
        if (!force && dark == IsDark) return;
        IsDark = dark;

        var app = System.Windows.Application.Current;
        if (app is null) return;

        var dictionaries = app.Resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString ?? "";
            if (source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        var themeDictionary = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };

        if (index >= 0) dictionaries[index] = themeDictionary;
        else dictionaries.Insert(0, themeDictionary);

        foreach (var reference in TrackedWindows)
        {
            if (reference.TryGetTarget(out var window)) ApplyWindowChrome(window);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyWindowChrome(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var useDark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // 旧版 Windows 不支持时忽略，仅影响标题栏颜色
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
