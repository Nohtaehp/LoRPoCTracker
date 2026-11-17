using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LoRPoCTracker.Config;

namespace LoRPoCTracker.Services;

/// <summary>
/// 卡牌图片本地缓存：读取 %LOCALAPPDATA%\LoRPoCTracker\images\{locale}\{code}.png。
/// 图片来自当前版本 PvE 资源包、网络标准卡牌（按需下载）以及缺口卡的占位卡面；
/// 本地缺失时按 Data Dragon 地址下载一次（离线/失败则返回 null）。
/// </summary>
public sealed class CardImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private readonly string _directory;
    private readonly Dictionary<string, ImageSource> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CardImageCache(string locale)
    {
        var language = string.IsNullOrWhiteSpace(locale) ? "zh_tw" : locale;
        _directory = Path.Combine(AppPaths.DataDir, "images", language);
    }

    /// <summary>本地图片目录。</summary>
    public string Directory => _directory;

    /// <summary>只查内存缓存（同步、无 IO）。</summary>
    public ImageSource? TryGetMemory(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        lock (_gate)
        {
            return _memory.TryGetValue(code, out var image) ? image : null;
        }
    }

    /// <summary>读取本地图片；不存在时若给了 imageUrl 就下载一次。</summary>
    public Task<ImageSource?> LoadAsync(string code, string? imageUrl = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return Task.FromResult<ImageSource?>(null);
        if (TryGetMemory(code) is { } cached) return Task.FromResult<ImageSource?>(cached);

        return _pending.GetOrAdd(code, _ => LoadCoreAsync(code, imageUrl));
    }

    /// <summary>清空内存缓存（重新生成本地图片后调用）。</summary>
    public void ClearMemory()
    {
        lock (_gate)
        {
            _memory.Clear();
        }
    }

    private async Task<ImageSource?> LoadCoreAsync(string code, string? imageUrl)
    {
        try
        {
            var path = Path.Combine(_directory, code + ".png");
            if (!File.Exists(path) && !string.IsNullOrWhiteSpace(imageUrl))
            {
                await DownloadAsync(imageUrl, path).ConfigureAwait(false);
            }

            if (!File.Exists(path)) return null;

            var image = await Task.Run(() => LoadBitmap(path)).ConfigureAwait(false);
            if (image is null)
            {
                // 图片损坏时删除，方便下次重新生成
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // 文件被占用时忽略
                }

                return null;
            }

            lock (_gate)
            {
                _memory[code] = image;
            }

            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(code, out _);
        }
    }

    private static async Task DownloadAsync(string url, string path)
    {
        try
        {
            // Data Dragon 的 assets 里是 http，统一走 https，避免明文被拦截
            if (url.StartsWith("http://dd.b.pvp.net/", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url["http://".Length..];

            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes.Length < 1024) return;

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, true);
        }
        catch
        {
            // 下载失败时保持缺失状态，下次再试
        }
    }

    private static ImageSource? LoadBitmap(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
