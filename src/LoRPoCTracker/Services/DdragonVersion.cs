using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoRPoCTracker.Config;

namespace LoRPoCTracker.Services;

/// <summary>
/// 解析在线图库版本号（形如 7_11_0），用于把 URL 里的版本段换成当前版本。
///
/// 两个独立来源交叉校验：本机游戏构建信息、在线资源清单内嵌版本。
///
/// 结果缓存在 data/ddragon_version.json（1 天，游戏构建分支变化时立即刷新）。
/// 两个来源都取不到时回退 latest，保证软件始终能工作。
/// </summary>
public sealed class DdragonVersion
{
    public const string Latest = "latest";
    public const string LatestRoot = "https://dd.b.pvp.net/latest";

    private const string CacheFileName = "ddragon_version.json";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);
    private static readonly Regex UrlVersionRegex = new(
        @"dd\.b\.pvp\.net/(\d+_\d+_\d+)/", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BranchRegex = new(
        @"^(\d+)[-_.](\d+)(?:[-_.]\d+)?", RegexOptions.Compiled);

    /// <summary>解析出的版本号；latest 表示回退。</summary>
    public string Version { get; private set; } = Latest;

    /// <summary>当前 Data Dragon 根地址（含版本）。</summary>
    public string Root => Version == Latest ? LatestRoot : $"https://dd.b.pvp.net/{Version}";

    public string GameBranch { get; private set; } = "";
    public string GameDir { get; private set; } = "";
    public string Source { get; private set; } = "fallback";

    /// <summary>本次解析出的版本与上次缓存不同（游戏/资料片更新）。</summary>
    public bool VersionChanged { get; private set; }

    /// <summary>ResolveAsync 顺带取回的 globals JSON，供 CardDatabase 复用，避免重复请求。</summary>
    public string GlobalsJson { get; private set; } = "";

    /// <summary>PvE 专属卡数据（单文件约 222KB，无需下载整包）。</summary>
    public string TpocDataUrl(string locale) => $"{Root}/tpoc/{locale}/data/tpoc-{locale}.json";

    /// <summary>PvE 专属卡整包（含卡图）。</summary>
    public string TpocBundleUrl(string locale) => $"{Root}/tpoc-{locale}.zip";

    private static string CachePath => Path.Combine(AppPaths.DataDir, CacheFileName);

    /// <summary>把 "7-11-release" / "7-11-15" 规整成 DDragon 的 "7_11_0"。</summary>
    public static string? NormalizeBranch(string? branch)
    {
        var m = BranchRegex.Match(branch ?? "");
        if (!m.Success) return null;
        return int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? $"{m.Groups[1].Value}_{minor}_0"
            : $"{m.Groups[1].Value}_{m.Groups[2].Value}_0";
    }

    /// <summary>从任意 Data Dragon JSON（globals / set / tpoc）的资源 URL 里提取版本号。</summary>
    public static string? ExtractFromDdragonJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var m = UrlVersionRegex.Match(json);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>读取本机客户端构建分支，转成 DDragon 版本号。</summary>
    public static bool TryReadGameVersion(string? configuredGameDir,
        out string version, out string branch, out string gameDir)
    {
        version = branch = gameDir = "";
        var live = GamePaths.DetectGameDir(configuredGameDir);
        if (live is null) return false;
        gameDir = live;

        var path = Path.Combine(live, "Game", "LoR_Data", "StreamingAssets", "ClientInternalConfig.json");
        if (!File.Exists(path)) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            branch = ReadString(root, "originalBuildBranch");
            version = NormalizeBranch(branch)
                      ?? NormalizeBranch(ReadString(root, "patchlineRefBuildId"))
                      ?? "";
            return version.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析并缓存版本：本机分支优先，CDN 内嵌版本校验，失败回退 latest。</summary>
    public async Task<string> ResolveAsync(HttpClient http, string locale, string? gameDir,
        CancellationToken ct = default)
    {
        var hasLocal = TryReadGameVersion(gameDir, out var local, out var branch, out var dir);
        GameBranch = branch;
        GameDir = dir;

        if (TryLoadCache(branch, out var cached))
        {
            Version = cached;
            Source = "cache";
            return Version;
        }

        try
        {
            GlobalsJson = await http.GetStringAsync(
                $"{LatestRoot}/core/{locale}/data/globals-{locale}.json", ct).ConfigureAwait(false);
        }
        catch
        {
            GlobalsJson = "";
        }

        var cdn = ExtractFromDdragonJson(GlobalsJson);
        if (!string.IsNullOrEmpty(cdn))
        {
            Version = cdn;
            Source = hasLocal && local == cdn ? "local+cdn" : "cdn";
        }
        else if (hasLocal)
        {
            Version = local;
            Source = "local";
        }
        else
        {
            Version = Latest;
            Source = "fallback";
        }

        var previous = ReadCachedVersion();
        VersionChanged = previous.Length > 0 &&
                         !string.Equals(previous, Version, StringComparison.OrdinalIgnoreCase);
        SaveCache();
        return Version;
    }

    private static string ReadCachedVersion()
    {
        try
        {
            if (!File.Exists(CachePath)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            return ReadString(doc.RootElement, "version");
        }
        catch
        {
            return "";
        }
    }

    private static bool TryLoadCache(string branch, out string version)
    {
        version = "";
        try
        {
            if (!File.Exists(CachePath)) return false;
            if (File.GetLastWriteTimeUtc(CachePath) < DateTime.UtcNow - CacheLifetime) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var cachedVersion = ReadString(doc.RootElement, "version");
            var cachedBranch = ReadString(doc.RootElement, "gameBranch");
            if (cachedVersion.Length == 0) return false;
            // 游戏更新（构建分支变化）时立即重新解析
            if (branch.Length > 0 && cachedBranch.Length > 0 &&
                !string.Equals(branch, cachedBranch, StringComparison.OrdinalIgnoreCase))
                return false;

            version = cachedVersion;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(new
            {
                version = Version,
                gameBranch = GameBranch,
                gameDir = GameDir,
                source = Source,
                checkedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // 缓存失败不影响主流程
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
