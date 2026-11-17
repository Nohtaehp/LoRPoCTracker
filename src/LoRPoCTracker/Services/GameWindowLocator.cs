using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LoRPoCTracker.Services;

/// <summary>定位 LoR 游戏窗口，供悬浮窗自动贴边。</summary>
public static class GameWindowLocator
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    /// <summary>DWM 扩展边框（不含窗口阴影），用于计算真正的视觉边缘。</summary>
    private const int DwmwaExtendedFrameBounds = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    public readonly record struct GameWindow(IntPtr Handle, Rect Bounds, Rect MonitorBounds, double Scale);

    public static GameWindow? Find()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("LoR");
        }
        catch
        {
            return null;
        }

        foreach (var process in processes)
        {
            try
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                if (!GetWindowRect(handle, out var rect)) continue;
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                // GetWindowRect 含不可见阴影边框，优先使用 DWM 的可视边界，贴边才能贴紧
                try
                {
                    if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out var frame,
                            Marshal.SizeOf<Rect>()) == 0 && frame.Width > 0 && frame.Height > 0)
                    {
                        rect = frame;
                    }
                }
                catch
                {
                    // dwmapi 不可用时退回 GetWindowRect
                }

                var monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                var hasMonitor = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
                var monitorRect = hasMonitor ? info.Monitor : rect;

                return new GameWindow(handle, rect, monitorRect, WindowEffects.GetDpiScale(handle));
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// 计算悬浮窗位置（DIP）：悬浮窗当前偏向游戏窗口哪一侧，就紧贴哪一侧；
    /// 该侧放不下时自动换到另一侧，仍放不下则贴显示器边缘。间距为 0（紧贴）。
    /// </summary>
    public static (double Left, double Top)? GetDockPosition(
        GameWindow game, double width, double height, double currentLeft)
    {
        var scale = game.Scale <= 0 ? 1.0 : game.Scale;
        var window = game.Bounds;
        var monitor = game.MonitorBounds;

        var overlayWidth = width * scale;
        var overlayHeight = height * scale;
        var overlayCenter = currentLeft * scale + overlayWidth / 2.0;
        var gameCenter = (window.Left + window.Right) / 2.0;

        var isFullscreen = window.Width >= monitor.Width * 0.95 && window.Height >= monitor.Height * 0.95;

        // 悬浮窗中心在游戏窗口中心左侧 => 贴左边，否则贴右边
        var preferLeft = overlayCenter < gameCenter;

        double left;
        if (isFullscreen)
        {
            left = preferLeft ? monitor.Left : monitor.Right - overlayWidth;
        }
        else
        {
            left = preferLeft
                ? window.Left - overlayWidth
                : window.Right;

            // 贴这一侧会超出显示器时换到另一侧
            if (left < monitor.Left || left + overlayWidth > monitor.Right)
            {
                left = preferLeft
                    ? window.Right
                    : window.Left - overlayWidth;
            }

            // 另一侧也放不下时贴显示器边缘
            if (left < monitor.Left) left = monitor.Left;
            else if (left + overlayWidth > monitor.Right) left = monitor.Right - overlayWidth;
        }

        var top = isFullscreen
            ? monitor.Top + (monitor.Height - overlayHeight) / 2.0
            : window.Top + 80;

        top = Math.Max(monitor.Top, Math.Min(top, monitor.Bottom - overlayHeight));

        return (left / scale, top / scale);
    }
}
