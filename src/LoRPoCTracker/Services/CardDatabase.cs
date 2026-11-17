using System.IO;
using System.Net.Http;
using System.Text.Json;
using LoRPoCTracker.Config;
using LoRPoCTracker.Models;

namespace LoRPoCTracker.Services;

/// <summary>
/// 从 Data Dragon 下载并缓存卡牌静态数据（名称/费用/地区等）。
/// 缓存文件：%LOCALAPPDATA%\LoRPoCTracker\cards_{locale}.json
/// </summary>
public sealed class CardDatabase
{
    private static readonly string[] SetRefs =
    {
        "set1", "set2", "set3", "set4", "set5", "set6", "set6cde",
        "set7", "set7b", "set8", "set9"
    };

    private readonly string _locale;
    private readonly string? _gameDir;
    private readonly HttpClient _http;
    private readonly DdragonVersion _version = new();
    private Dictionary<string, CardInfo> _cards = new(StringComparer.OrdinalIgnoreCase);

    public CardDatabase(string locale, string? gameDir = null)
    {
        _locale = string.IsNullOrWhiteSpace(locale) ? "zh_tw" : locale;
        _gameDir = gameDir;

        var handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = true };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(40)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LoRPoCTracker");
    }

    /// <summary>当前 Data Dragon 版本号（如 7_11_0）；解析失败为 latest。</summary>
    public string Version => _version.Version;

    /// <summary>当前 Data Dragon 根地址。</summary>
    public string DdragonRootUrl => _version.Root;

    /// <summary>PvE 专属卡数据地址（单文件，无需下载整包）。</summary>
    public string TpocDataUrl => _version.TpocDataUrl(_locale);

    /// <summary>PvE 专属卡整包地址。</summary>
    public string TpocBundleUrl => _version.TpocBundleUrl(_locale);

    public int Count => _cards.Count;

    public bool IsLoaded => _cards.Count > 0;

    public string Locale => _locale;

    /// <summary>全部卡牌代码。</summary>
    public IEnumerable<string> AllCodes => _cards.Keys;

    /// <summary>全部卡牌数据（快照）。</summary>
    public IReadOnlyList<CardInfo> AllCards => _cards.Values.ToList();

    /// <summary>
    /// 合并一份 Data Dragon / PvE(tpoc) 的卡牌 JSON，返回卡数。
    /// forceCollectible 用于 tpoc：官方把 PvE 卡全标成 collectible=false，
    /// 但记牌器里它们是实际出牌，不能被当成衍生物从 Boss 牌库剔除。
    /// </summary>
    public int MergeCardsJson(string json, bool forceCollectible = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;

        var parsed = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);
        ParseSetJson(json, parsed);
        foreach (var info in parsed.Values)
        {
            if (forceCollectible)
            {
                info.Collectible = true;
                info.HasCollectibleField = false;
            }

            _cards[info.Code] = info;
        }

        return parsed.Count;
    }

    /// <summary>补入本地化兜底卡（已存在的官方卡不受影响）。</summary>
    public void UpsertCards(IEnumerable<CardInfo> cards)
    {
        foreach (var info in cards)
        {
            if (string.IsNullOrWhiteSpace(info.Code)) continue;
            if (_cards.ContainsKey(info.Code)) continue;
            _cards[info.Code] = info;
        }
    }

    /// <summary>把合并后的完整卡牌数据写回缓存，下次启动直接可用。</summary>
    public void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.CardCachePath(_locale), JsonSerializer.Serialize(_cards));
        }
        catch
        {
            // 缓存写入失败不影响本次运行
        }
    }

    public bool TryGet(string code, out CardInfo info)
    {
        if (string.IsNullOrEmpty(code))
        {
            info = null!;
            return false;
        }

        return _cards.TryGetValue(code, out info!);
    }

    public string GetName(string code) => TryGet(code, out var info) ? info.Name : code;

    /// <summary>
    /// 在线图库未收录的 PoC 专属卡（99PZ 系列）静态补充数据。
    /// 名称与说明由游戏本地化补齐，这里补费用、攻防、类型、稀有度、地区色和卡图。
    /// </summary>
    private static readonly (string Code, int Cost, string Type, string Rarity, int? Attack, int? Health)[] PocCards =
    {
        ("99PZ101", 3, "單位", "稀有", 3, 3),
        ("99PZ102", 3, "單位", "", 4, 2),
        ("99PZ103", 3, "單位", "", 1, 1),
        ("99PZ103T1", 1, "單位", "", 3, 2),
        ("99PZ104", 2, "單位", "", 3, 3),
        ("99PZ106", 3, "單位", "", 0, 4),
        ("99PZ109", 4, "單位", "稀有", 5, 3),
        ("99PZ110", 3, "單位", "", 3, 2),
        ("99PZ111", 3, "單位", "", 2, 2),
        ("99PZ112", 3, "單位", "稀有", 2, 2),
        ("99PZ113", 1, "單位", "稀有", 1, 2),
        ("99PZ114", 1, "單位", "", 2, 1),
        ("99PZ115", 3, "地標", "史詩", null, null),
        ("99PZ116", 4, "單位", "稀有", 5, 3),
        ("99PZ117", 4, "單位", "", 5, 3),
        ("99PZ118", 1, "單位", "", 1, 1),
        ("99PZ119", 1, "單位", "", 2, 1),
        ("99PZ120", 4, "法術", "", null, null),
        ("99PZ121", 2, "法術", "", null, null),
        ("99PZ124", 2, "法術", "史詩", null, null),
        ("99PZ125", 2, "法術", "史詩", null, null),
        ("99PZ126", 1, "法術", "稀有", null, null),
        ("99PZ127", 6, "法術", "史詩", null, null),
        ("99PZ128", 2, "法術", "稀有", null, null),
        ("99PZ129", 0, "法術", "史詩", null, null),
        ("99PZ130", 2, "法術", "", null, null),
        ("99PZ131", 3, "法術", "史詩", null, null)
    };

    /// <summary>在线图库未收录的 PvE 专属卡本地补充数据。</summary>
    private static Dictionary<string, CardInfo>? _extraCards;
    private static readonly object ExtraGate = new();

    /// <summary>查内置补充数据（Data Dragon 查不到时使用）。</summary>
    public static bool TryGetFallback(string code, out CardInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(code)) return false;

        if (GetExtraCards().TryGetValue(code, out var extra))
        {
            info = extra;
            return true;
        }

        foreach (var (cardCode, cost, type, rarity, attack, health) in PocCards)
        {
            if (!string.Equals(cardCode, code, StringComparison.OrdinalIgnoreCase)) continue;

            info = new CardInfo
            {
                Code = cardCode,
                Cost = cost,
                Attack = attack,
                Health = health,
                Type = type,
                Rarity = rarity,
                Region = "皮爾特沃夫與佐恩",
                RegionRef = "PiltoverZaun",
                ImageUrl = $"https://wiki.leagueoflegends.com/en-us/images/{cardCode}.png"
            };
            return true;
        }

        return false;
    }

    private static Dictionary<string, CardInfo> GetExtraCards()
    {
        lock (ExtraGate)
        {
            if (_extraCards is not null) return _extraCards;

            var map = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(AppPaths.DataDir, "poc_extra_cards.json");
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var row in doc.RootElement.EnumerateArray())
                    {
                        var code = row.TryGetProperty("code", out var ce) ? ce.GetString() ?? "" : "";
                        if (code.Length == 0) continue;

                        var kind = row.TryGetProperty("kind", out var ke) ? ke.GetString() ?? "" : "";
                        int? ReadInt(string name) => row.TryGetProperty(name, out var e) &&
                                                     e.ValueKind == JsonValueKind.Number
                            ? e.GetInt32()
                            : null;
                        List<string> ReadList(string name)
                        {
                            var list = new List<string>();
                            if (row.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in e.EnumerateArray())
                                {
                                    var text = item.GetString();
                                    if (!string.IsNullOrWhiteSpace(text)) list.Add(text);
                                }
                            }

                            return list;
                        }

                        var regionRefs = ReadList("region_refs");
                        var typeId = row.TryGetProperty("type_id", out var te) &&
                                     te.ValueKind == JsonValueKind.Number
                            ? te.GetInt32()
                            : 0;
                        map[code] = new CardInfo
                        {
                            Code = code,
                            Name = row.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "",
                            Cost = ReadInt("cost") ?? 0,
                            Attack = ReadInt("attack"),
                            Health = ReadInt("health"),
                            Type = typeId switch
                            {
                                3 => "法術",
                                6 => "技能",
                                7 => "地標",
                                8 => "法術",
                                10 => "裝備",
                                _ => kind switch
                                {
                                    "unit" => "單位",
                                    "spell" => "法術",
                                    "landmark" => "地標",
                                    _ => ""
                                }
                            },
                            RegionRef = regionRefs.FirstOrDefault() ??
                                        (row.TryGetProperty("region_ref", out var re) ? re.GetString() ?? "" : ""),
                            RegionRefs = regionRefs,
                            SpellSpeed = row.TryGetProperty("spell_speed", out var se) ? se.GetString() ?? "" : "",
                            Keywords = ReadList("keywords"),
                            Subtypes = ReadList("subtypes"),
                            DataDragonCode = row.TryGetProperty("dd_code", out var de) ? de.GetString() ?? "" : ""
                        };
                    }
                }
            }
            catch
            {
                // 文件缺失/损坏时退回内置小表
            }

            _extraCards = map;
            return map;
        }
    }

    /// <summary>游戏内独占卡代码（poc_extra_cards.json 中的全部条目）。</summary>
    public static IEnumerable<string> ExtraCardCodes => GetExtraCards().Keys;

    /// <summary>该游戏内卡是否已有 Data Dragon 对应卡（网络已有，不需要本地拼图）。</summary>
    public static bool IsDataDragonDuplicate(string code) =>
        GetExtraCards().TryGetValue(code, out var info) &&
        !string.IsNullOrWhiteSpace(info.DataDragonCode);

    /// <summary>按名称或代码搜索卡牌。</summary>
    public IReadOnlyList<CardInfo> Search(string query, int limit = 40)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<CardInfo>();

        var text = query.Trim();
        return _cards.Values
            .Where(card =>
                card.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                card.Code.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(card => card.Name, StringComparer.CurrentCulture)
            .Take(limit)
            .ToList();
    }

    /// <summary>优先读缓存；缓存缺失或过期时重新下载。</summary>
    public async Task LoadAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // 先解析版本（本机分支 + CDN 校验，结果缓存 1 天），后续所有 URL 都用锁定版本
        try
        {
            await _version.ResolveAsync(_http, _locale, _gameDir, ct).ConfigureAwait(false);
            progress?.Report($"Data Dragon 版本 {_version.Version}（{_version.Source}）");
        }
        catch
        {
            // 版本解析失败时 ResolveAsync 已回退到 latest，不阻断卡牌加载
        }

        var cachePath = AppPaths.CardCachePath(_locale);
        var cacheValid = File.Exists(cachePath) &&
                         File.GetLastWriteTimeUtc(cachePath) > DateTime.UtcNow.AddDays(-7) &&
                         !_version.VersionChanged; // 版本更新时标准卡数据也要跟着刷新

        if (cacheValid)
        {
            try
            {
                var json = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
                var cached = JsonSerializer.Deserialize<Dictionary<string, CardInfo>>(json);
                if (cached is { Count: > 0 })
                {
                    _cards = new Dictionary<string, CardInfo>(cached, StringComparer.OrdinalIgnoreCase);
                    progress?.Report($"已加载本地卡牌数据（{_cards.Count} 张）");
                    return;
                }
            }
            catch
            {
                // 缓存损坏，继续走网络
            }
        }

        try
        {
            var downloaded = await DownloadAllAsync(progress, ct).ConfigureAwait(false);
            if (downloaded.Count > 0)
            {
                _cards = downloaded;
                Directory.CreateDirectory(AppPaths.DataDir);
                var json = JsonSerializer.Serialize(_cards);
                await File.WriteAllTextAsync(cachePath, json, ct).ConfigureAwait(false);
                progress?.Report($"卡牌数据已更新（{_cards.Count} 张）");
                return;
            }
        }
        catch (Exception ex)
        {
            progress?.Report("卡牌数据下载失败：" + ex.Message);
        }

        // 网络失败时尽量用旧缓存
        if (File.Exists(cachePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
                var cached = JsonSerializer.Deserialize<Dictionary<string, CardInfo>>(json);
                if (cached is { Count: > 0 })
                {
                    _cards = new Dictionary<string, CardInfo>(cached, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task<Dictionary<string, CardInfo>> DownloadAllAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);

        var sets = await GetSetRefsAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < sets.Count; i++)
        {
            var setRef = sets[i];
            progress?.Report($"正在下载卡牌数据 {i + 1}/{sets.Count}（{setRef}）…");

            var url = $"{_version.Root}/{setRef}/{_locale}/data/{setRef}-{_locale}.json";
            try
            {
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                ParseSetJson(json, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 部分扩展包（如活动包）在 CDN 上不可用，跳过即可
            }
        }

        return result;
    }

    private async Task<List<string>> GetSetRefsAsync(CancellationToken ct)
    {
        try
        {
            // 版本解析时已经取过 globals，直接复用，避免重复请求
            var json = _version.GlobalsJson;
            if (string.IsNullOrEmpty(json))
            {
                var url = $"{_version.Root}/core/{_locale}/data/globals-{_locale}.json";
                json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sets", out var sets) && sets.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in sets.EnumerateArray())
                {
                    if (!item.TryGetProperty("nameRef", out var nameRef)) continue;
                    var name = nameRef.GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var setRef = name.Trim().ToLowerInvariant();
                    if (!list.Contains(setRef)) list.Add(setRef);
                }

                if (list.Count > 0) return list;
            }
        }
        catch
        {
            // 拿不到 globals 时使用内置列表
        }

        return SetRefs.ToList();
    }

    private static void ParseSetJson(string json, Dictionary<string, CardInfo> target)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var code = GetString(element, "cardCode");
            if (string.IsNullOrWhiteSpace(code)) continue;

            var info = new CardInfo
            {
                Code = code,
                Name = GetString(element, "name"),
                Cost = GetInt(element, "cost"),
                Attack = GetNullableInt(element, "attack"),
                Health = GetNullableInt(element, "health"),
                Type = GetString(element, "type"),
                SuperType = GetString(element, "supertype"),
                Rarity = GetString(element, "rarity"),
                Region = GetStringList(element, "regions").FirstOrDefault() ?? GetString(element, "region"),
                RegionRef = GetStringList(element, "regionRefs").FirstOrDefault() ?? GetString(element, "regionRef"),
                RegionRefs = GetStringList(element, "regionRefs"),
                Description = GetString(element, "descriptionRaw"),
                FlavorText = GetString(element, "flavorText"),
                LevelUpDescription = GetString(element, "levelupDescriptionRaw"),
                SpellSpeed = GetString(element, "spellSpeed"),
                Subtypes = GetStringList(element, "subtypes"),
                SubtypeRefs = GetStringList(element, "subtypeRefs"),
                KeywordRefs = GetStringList(element, "keywordRefs"),
                Collectible = GetBool(element, "collectible"),
                HasCollectibleField = element.TryGetProperty("collectible", out _)
            };

            if (element.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Array)
            {
                info.Keywords = keywords.EnumerateArray()
                    .Where(k => k.ValueKind == JsonValueKind.String)
                    .Select(k => k.GetString() ?? "")
                    .Where(k => k.Length > 0)
                    .ToList();
            }

            if (element.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("gameAbsolutePath", out var path) &&
                        path.ValueKind == JsonValueKind.String)
                    {
                        info.ImageUrl = path.GetString() ?? "";
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(info.Name)) info.Name = code;
            target[code] = info;
        }
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), out var i) ? i : 0,
            _ => 0
        };
    }

    private static int? GetNullableInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(value.GetString(), out var i) ? i : null,
            _ => null
        };
    }

    private static List<string> GetStringList(JsonElement element, string name)
    {
        var list = new List<string>();
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text)) list.Add(text);
        }

        return list;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return true;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => true
        };
    }
}
