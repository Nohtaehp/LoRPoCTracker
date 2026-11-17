using System.Runtime.InteropServices;

namespace LoRPoCTracker.Services;

/// <summary>
/// 单实例控制：第二个实例启动时广播激活消息并退出，
/// 已运行的实例收到消息后把悬浮窗从托盘/隐藏状态唤出。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\LoRPoCTracker.SingleInstance";
    private const int HwndBroadcast = 0xFFFF;

    private static Mutex? _mutex;
    private static readonly uint ActivationMessageId = RegisterWindowMessage("LoRPoCTracker.ActivateWindow");

    /// <summary>系统全局唯一的消息 ID；同名消息在各进程内一致。</summary>
    public static int ActivateMessage => (int)ActivationMessageId;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>返回 false 表示已有实例在运行，且已广播激活请求。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (createdNew) return true;

        _mutex.Dispose();
        _mutex = null;

        if (ActivationMessageId == 0) return false;

        // 已运行的实例可能尚未创建窗口，广播几次提高命中率
        for (var i = 0; i < 3; i++)
        {
            PostMessage(HwndBroadcast, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(200);
        }

        return false;
    }
}
