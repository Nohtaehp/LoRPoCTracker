using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LoRPoCTracker.Config;

namespace LoRPoCTracker.Services;

/// <summary>
/// 大地图 Boss 头像：本地插画 → 按需从游戏资源提取 → 官方卡图兜底，
/// 裁成方形后缓存到 data/avatars/{locale}/{CODE}.png。
/// </summary>
public sealed class BossAvatarService
{
    private const int AvatarSize = 128;

    private readonly CardImageCache _images;
    private readonly string? _gameDir;
    private readonly string _avatarDir;

    private readonly Dictionary<string, ImageSource?> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _extractGate = new(1, 1);
    private readonly object _gate = new();

    /// <summary>已尝试从游戏生成过、但游戏里没有插画的卡（避免反复扫描资源）。</summary>
    private readonly HashSet<string> _extractAttempted = new(StringComparer.OrdinalIgnoreCase);

    private List<(string Squashed, string Path)>? _illustrationIndex;

    public BossAvatarService(CardImageCache images, string? gameDir, string locale)
    {
        _images = images;
        _gameDir = gameDir;
        var language = string.IsNullOrWhiteSpace(locale) ? "zh_tw" : locale;
        _avatarDir = Path.Combine(AppPaths.DataDir, "avatars", language);
    }

    /// <summary>本地插画目录（游戏原始插画，与地图节点同源）。</summary>
    private static string IllustrationDir => Path.Combine(AppPaths.DataDir, "local_assets", "illustrations");

    /// <summary>只查内存缓存（同步、无 IO）。</summary>
    public ImageSource? TryGetMemory(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        lock (_gate)
        {
            return _memory.TryGetValue(code, out var image) ? image : null;
        }
    }

    /// <summary>清空内存缓存（卡牌资源重新导入后调用，允许重新生成头像）。</summary>
    public void ClearMemory()
    {
        lock (_gate)
        {
            _memory.Clear();
            _extractAttempted.Clear();
        }

        _illustrationIndex = null;
    }

    /// <summary>
    /// 批量预热：一次性把缺失的插画从游戏资源生成出来（首次进地图时调用）。
    /// </summary>
    public async Task PrewarmAsync(IEnumerable<string> codes)
    {
        List<string> missing;
        lock (_gate)
        {
            missing = codes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(code => !_extractAttempted.Contains(code))
                .Where(code => FindIllustration(code) is null)
                .ToList();

            // 提前登记，避免同一张卡被多个头像请求重复生成
            foreach (var code in missing) _extractAttempted.Add(code);
        }

        if (missing.Count == 0) return;

        await _extractGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 资源扫描与读取都是同步重活，必须放到线程池，避免卡住界面
            var extracted = await Task.Run(
                () => GameAssetExtractor.ExtractIllustrationsAsync(_gameDir, missing)).ConfigureAwait(false);

            if (extracted > 0)
            {
                _illustrationIndex = null;
                CardImageComposer.ResetRoiCache();
            }
        }
        catch
        {
            // 生成失败时退回卡图 / 占位头像
        }
        finally
        {
            _extractGate.Release();
        }
    }

    /// <summary>读取 Boss 头像；本地没有插画时按需从游戏生成，最后退回官方卡图。</summary>
    public Task<ImageSource?> LoadAsync(string code, string? imageUrl = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return Task.FromResult<ImageSource?>(null);

        // 值为 null 表示「已尝试且确实没有图」，同样算缓存命中，避免反复重试
        lock (_gate)
        {
            if (_memory.TryGetValue(code, out var cached)) return Task.FromResult(cached);
        }

        return _pending.GetOrAdd(code, key => LoadCoreAsync(key, imageUrl));
    }

    private async Task<ImageSource?> LoadCoreAsync(string code, string? imageUrl)
    {
        try
        {
            var avatarPath = Path.Combine(_avatarDir, code.ToUpperInvariant() + ".png");
            if (File.Exists(avatarPath))
            {
                var cached = LoadBitmap(avatarPath);
                if (cached is not null)
                {
                    Remember(code, cached);
                    return cached;
                }
            }

            // 1. 本地插画（与游戏地图节点使用的原画一致）
            var artPath = FindIllustration(code);
            if (artPath is null)
            {
                await PrewarmAsync(new[] { code }).ConfigureAwait(false);
                artPath = FindIllustration(code);
            }

            if (artPath is not null)
            {
                var avatar = await Task.Run(() => ComposeFromIllustration(artPath, code)).ConfigureAwait(false);
                if (avatar is not null)
                {
                    await SaveAsync(avatar, avatarPath).ConfigureAwait(false);
                    Remember(code, avatar);
                    return avatar;
                }
            }

            // 2. 官方卡图兜底：取卡面上方的插画区域
            var card = await _images.LoadAsync(code, imageUrl).ConfigureAwait(false);
            if (card is not null)
            {
                var avatar = await Task.Run(() => ComposeFromCard(card)).ConfigureAwait(false);
                if (avatar is not null)
                {
                    await SaveAsync(avatar, avatarPath).ConfigureAwait(false);
                    Remember(code, avatar);
                    return avatar;
                }
            }

            Remember(code, null);
            return null;
        }
        catch
        {
            Remember(code, null);
            return null;
        }
        finally
        {
            _pending.TryRemove(code, out _);
        }
    }

    private void Remember(string code, ImageSource? image)
    {
        lock (_gate)
        {
            _memory[code] = image;
        }
    }

    // ---------------------------------------------------------------- 图像处理

    /// <summary>插画按 IllustrationProfile 的取景参数裁成方形；没有取景参数时取中心偏上区域。</summary>
    private static BitmapSource? ComposeFromIllustration(string path, string code)
    {
        var art = LoadBitmap(path);
        if (art is null || art.PixelWidth <= 0 || art.PixelHeight <= 0) return null;

        var w = art.PixelWidth;
        var h = art.PixelHeight;
        var side = Math.Min(w, h) * 0.62;
        var cx = w / 2.0;
        var cy = h * 0.40;

        var roi = CardImageComposer.FindRoi(code);
        if (roi is { } r)
        {
            var viewHeight = Math.Min(h, Math.Max(h * 0.05, r.Z * h));
            side = Math.Min(Math.Min(w, h), viewHeight);
            cx = r.X * w;
            cy = r.Y * h;
        }

        return CropSquare(art, cx, cy, side);
    }

    /// <summary>官方卡面兜底：卡面顶部约一半是插画，取居中方形避开卡框与说明文字。</summary>
    private static BitmapSource? ComposeFromCard(ImageSource card)
    {
        if (card is not BitmapSource source || source.PixelWidth <= 0) return null;

        var side = Math.Min(source.PixelWidth, source.PixelHeight * 0.46);
        return CropSquare(source, source.PixelWidth / 2.0, source.PixelHeight * 0.27, side);
    }

    private static BitmapSource CropSquare(BitmapSource source, double cx, double cy, double side)
    {
        var size = (int)Math.Round(side);
        size = Math.Clamp(size, 8, Math.Min(source.PixelWidth, source.PixelHeight));

        var x = (int)Math.Round(cx - size / 2.0);
        var y = (int)Math.Round(cy - size / 2.0);
        x = Math.Clamp(x, 0, Math.Max(0, source.PixelWidth - size));
        y = Math.Clamp(y, 0, Math.Max(0, source.PixelHeight - size));

        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, size, size));
        cropped.Freeze();

        if (size == AvatarSize) return cropped;

        var scale = (double)AvatarSize / size;
        var scaled = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static async Task SaveAsync(BitmapSource image, string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                var tmp = path + ".tmp";
                using (var stream = File.Create(tmp))
                {
                    encoder.Save(stream);
                }

                File.Move(tmp, path, true);
            }).ConfigureAwait(false);
        }
        catch
        {
            // 头像缓存写失败时仍可显示内存中的图
        }
    }

    private static BitmapImage? LoadBitmap(string path)
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

    // ---------------------------------------------------------------- 插画文件定位

    private string? FindIllustration(string code)
    {
        var directory = IllustrationDir;
        if (!Directory.Exists(directory)) return null;

        var upper = Path.Combine(directory, code.ToUpperInvariant() + ".png");
        if (File.Exists(upper)) return upper;

        var lower = Path.Combine(directory, code.ToLowerInvariant() + ".png");
        if (File.Exists(lower)) return lower;

        _illustrationIndex ??= Directory.EnumerateFiles(directory, "*.png")
            .Select(path => (Squashed: Squash(Path.GetFileNameWithoutExtension(path)), Path: path))
            .ToList();

        var target = Squash(code);
        foreach (var (name, path) in _illustrationIndex)
        {
            if (name.Contains(target, StringComparison.Ordinal)) return path;
        }

        return null;
    }

    private static string Squash(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }

        return builder.ToString();
    }
}
