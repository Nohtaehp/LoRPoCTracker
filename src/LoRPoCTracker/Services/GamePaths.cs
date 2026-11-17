using System.Diagnostics;
using System.IO;

namespace LoRPoCTracker.Services;

/// <summary>定位 LoR 安装目录（用于读取本地化文本与游戏数据）。</summary>
public static class GamePaths
{
    public static string? DetectGameDir(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        try
        {
            foreach (var process in Process.GetProcessesByName("LoR"))
            {
                try
                {
                    var exe = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exe)) continue;

                    var gameDir = Path.GetDirectoryName(exe);
                    var liveDir = gameDir is null ? null : Path.GetDirectoryName(gameDir);
                    if (liveDir is not null && Directory.Exists(liveDir)) return liveDir;
                }
                catch
                {
                    // 进程权限不足时忽略
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // ignore
        }

        foreach (var root in new[] { "C:", "D:", "E:", "F:" })
        {
            var candidate = Path.Combine(root + "\\", "Riot Games", "LoR", "live");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }
}
