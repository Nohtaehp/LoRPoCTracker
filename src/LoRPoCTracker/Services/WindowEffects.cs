using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LoRPoCTracker.Services;

/// <summary>窗口置顶 / 鼠标穿透 / 层级等 Win32 辅助方法。</summary>
public static class WindowEffects
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public static IntPtr GetHandle(Window window)
    {
        var helper = new WindowInteropHelper(window);
        return helper.Handle;
    }

    /// <summary>设置为工具窗口：不显示在 Alt+Tab / 任务栏。</summary>
    public static void ApplyOverlayStyles(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExToolWindow;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
    }

    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if (enabled)
        {
            // 穿透状态下同时禁用激活，避免点击穿透层时抢走游戏焦点
            style |= WsExTransparent | WsExLayered | WsExNoActivate;
        }
        else
        {
            style &= ~(WsExTransparent | WsExNoActivate);
        }

        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
    }

    /// <summary>周期性重申置顶，防止被游戏窗口抢走层级。</summary>
    public static void ReassertTopmost(Window window)
    {
        SetTopmost(GetHandle(window));
    }

    /// <summary>把指定窗口句柄置顶（也用于 ToolTip 弹窗）。</summary>
    public static void SetTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public static double GetDpiScale(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi <= 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }
}
