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
    /// 计算悬浮窗位置（DIP）。全屏时贴在屏幕左侧，窗口化时贴在游戏窗口右侧外侧。
    /// </summary>
    public static (double Left, double Top)? GetDockPosition(GameWindow game, double width, double height)
    {
        var scale = game.Scale <= 0 ? 1.0 : game.Scale;
        var window = game.Bounds;
        var monitor = game.MonitorBounds;

        var isFullscreen = window.Width >= monitor.Width * 0.95 && window.Height >= monitor.Height * 0.95;

        double left;
        double top;

        if (isFullscreen)
        {
            left = monitor.Left + 12;
            top = monitor.Top + (monitor.Height - height * scale) / 2.0;
        }
        else
        {
            left = window.Right + 8;
            // 右侧放不下时改放左侧
            if (left + width * scale > monitor.Right - 8)
            {
                left = window.Left - width * scale - 8;
            }

            top = window.Top + 80;
        }

        top = Math.Max(monitor.Top + 8, Math.Min(top, monitor.Bottom - height * scale - 8));

        return (left / scale, top / scale);
    }
}
