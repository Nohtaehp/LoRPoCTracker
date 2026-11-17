using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoRPoCTracker.Config;
using LoRPoCTracker.Models;

namespace LoRPoCTracker.Services;

/// <summary>一次网络源加载结果。</summary>
public sealed record SourceLoadResult(
    IReadOnlyList<EncounterRecord> Records,
    int FileCount,
    int DownloadedFiles,
    int CachedFiles,
    int FailedFiles,
    string Message);

/// <summary>
/// 网络数据源加载器：一个源可配置多个 json 直链（多文件处理），逐个下载并合并；
/// 每个文件按 URL 缓存在 data/sources/{id}/ 下，断网或链接失效时回退本地缓存。
/// 客户端只发 GET，不写回仓库，也不携带任何凭据。
/// </summary>
public static class EncounterSourceLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<SourceLoadResult> LoadAsync(DataSourceConfig source, bool forceRefresh,
        CancellationToken ct = default)
    {
        var urls = (source.Files ?? new List<string>())
            .Where(IsValidUrl)
            .Select(file => file.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (urls.Count == 0)
        {
            return new SourceLoadResult(Array.Empty<EncounterRecord>(), 0, 0, 0, 0,
                "该网络源还没有配置有效的文件地址（需要 http/https 直链）");
        }

        var records = new List<EncounterRecord>();
        var downloaded = 0;
        var cached = 0;
        var failed = 0;

        foreach (var url in urls)
        {
            var cachePath = CachePath(source.Id, url);
            var json = forceRefresh ? null : TryReadCache(cachePath);

            if (json is not null)
            {
                cached++;
            }
            else
            {
                var download = await DownloadAsync(url, forceRefresh, ct);
                if (download is not null)
                {
                    json = download;
                    WriteCache(cachePath, json);
                    downloaded++;
                }
                else
                {
                    json = TryReadCache(cachePath);
                    if (json is not null)
                    {
                        cached++;
                    }
                    else
                    {
                        failed++;
                        continue;
                    }
                }
            }

            var parsed = Parse(json);
            if (parsed is not null) records.AddRange(parsed);
        }

        var message = $"下载 {downloaded} 个、缓存 {cached} 个" +
                      (failed > 0 ? $"、失败 {failed} 个" : "") +
                      $"，共 {records.Count} 条关卡录制";
        return new SourceLoadResult(records, urls.Count, downloaded, cached, failed, message);
    }

    /// <summary>把多个文件里的同名关卡合并：卡牌取最大张数，定档取并集，名称/预定取较完整的一份。</summary>
    public static Dictionary<string, EncounterRecord> Merge(IEnumerable<EncounterRecord> records)
    {
        var result = new Dictionary<string, EncounterRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Key)) continue;

            if (!result.TryGetValue(record.Key, out var target))
            {
                result[record.Key] = Clone(record);
                continue;
            }

            MergeInto(target, record);
        }

        return result;
    }

    /// <summary>删除某个网络源的全部缓存文件。</summary>
    public static void ClearCache(string sourceId)
    {
        try
        {
            var dir = CacheDirFor(sourceId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 缓存清理失败不影响使用
        }
    }

    private static bool IsValidUrl(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return false;
        return Uri.TryCreate(file.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static async Task<string?> DownloadAsync(string url, bool forceRefresh, CancellationToken ct)
    {
        try
        {
            // GitHub raw 等直链可能有 CDN 缓存，强制刷新时附加时间戳尝试绕过；
            // jsDelivr 的分支缓存不受此参数影响，由 CDN 自行按 12 小时更新
            var requestUrl = forceRefresh
                ? url + (url.Contains('?') ? "&" : "?") + "_=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                : url;

            using var response = await Http.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            // 先验证格式，避免把登录页 / 404 页面写进缓存
            return Parse(json) is null ? null : json;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<EncounterRecord>? Parse(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<EncounterDatabaseFile>(json, JsonOptions);
            return data?.Encounters?.Values;
        }
        catch
        {
            return null;
        }
    }

    private static void MergeInto(EncounterRecord target, EncounterRecord other)
    {
        foreach (var (code, count) in other.Cards ?? new Dictionary<string, int>())
        {
            target.Cards[code] = Math.Max(target.Cards.GetValueOrDefault(code), count);
        }

        foreach (var token in other.TokenCards ?? new List<string>())
        {
            if (!target.IsToken(token)) target.TokenCards.Add(token);
        }

        target.IsFinalized |= other.IsFinalized;
        target.ExpectedCount = Math.Max(target.ExpectedCount, other.ExpectedCount);
        if (other.LastSeen > target.LastSeen) target.LastSeen = other.LastSeen;
        if (other.FirstSeen != default && (target.FirstSeen == default || other.FirstSeen < target.FirstSeen))
            target.FirstSeen = other.FirstSeen;
        if (string.IsNullOrWhiteSpace(target.Campaign)) target.Campaign = other.Campaign;
        if (string.IsNullOrWhiteSpace(target.MapName)) target.MapName = other.MapName;
        if (string.IsNullOrWhiteSpace(target.NodeName)) target.NodeName = other.NodeName;
    }

    private static EncounterRecord Clone(EncounterRecord record) => new()
    {
        Key = record.Key,
        Campaign = record.Campaign,
        MapName = record.MapName,
        NodeName = record.NodeName,
        FirstSeen = record.FirstSeen,
        LastSeen = record.LastSeen,
        ExpectedCount = record.ExpectedCount,
        IsFinalized = record.IsFinalized,
        Cards = record.Cards is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(record.Cards, StringComparer.OrdinalIgnoreCase),
        TokenCards = record.TokenCards is null ? new List<string>() : new List<string>(record.TokenCards)
    };

    private static string? TryReadCache(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch
        {
            // 缓存写失败不影响本次显示
        }
    }

    private static string CachePath(string sourceId, string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        return Path.Combine(CacheDirFor(sourceId), hash + ".json");
    }

    private static string CacheDirFor(string sourceId)
    {
        var safe = new string(sourceId.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
        return Path.Combine(AppPaths.DataDir, "sources", string.IsNullOrWhiteSpace(safe) ? "unknown" : safe);
    }
}
