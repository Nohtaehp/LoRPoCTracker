using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoRPoCTracker.Config;
using LoRPoCTracker.Models;

namespace LoRPoCTracker.Services;

/// <summary>
/// 把「网络标准卡牌 + PvE(tpoc) 卡牌」整合成本地可预览的完整卡牌库。
///
/// 资源来源（全部按需下载）：
///   - 标准卡：在线图源（CardImageCache 按需下载）
///   - 98 系列：在线单图
///   - 99 系列：由程序从游戏资源生成卡面
///
/// PvE 卡牌数据（名称/费用/描述/图片地址）按需获取并缓存（约 200KB）。
/// </summary>
public sealed class CardAssetImporter
{
    public sealed record ImportReport(
        string Version,
        string Locale,
        bool BundleDownloaded,
        int PveCards,
        int TotalCards,
        int LocalImages,
        int PveImages,
        int AliasImages,
        int SameNameImages,
        int StandardImagesDownloaded,
        int NetworkImageCards);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly Regex CardCodeRegex = new(@"^\d{2}[A-Za-z]{2}\d{3}", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex BracketRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex ValueRegex = new(@"\{[^{}:]+:([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"\{[^{}]*\}", RegexOptions.Compiled);
    private static readonly Regex VariantRegex = new(@"T\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly CardDatabase _cards;
    private readonly LocalizationDatabase _loc;
    private readonly string _locale;
    private readonly string _version;

    public CardAssetImporter(CardDatabase cards, LocalizationDatabase loc)
    {
        _cards = cards;
        _loc = loc;
        _locale = cards.Locale;
        _version = string.IsNullOrWhiteSpace(cards.Version) ? DdragonVersion.Latest : cards.Version;
    }

    private static string LatestTpocRoot => "https://dd.b.pvp.net/latest/tpoc";

    /// <summary>PvE 卡牌数据缓存（单文件）。</summary>
    public string TpocJsonPath => Path.Combine(AppPaths.DataDir, $"tpoc-{_locale}.json");

    public string CardImageDir => Path.Combine(AppPaths.DataDir, "images", _locale);

    public string PveImageUrl(string code) => $"{LatestTpocRoot}/{_locale}/img/cards/{code}.png";

    /// <summary>
    /// 幂等导入：下载 PvE 数据、合并卡牌数据、落地 98 系列卡图、补齐描述。
    /// allStandardImages=true 时额外把所有标准卡图批量下载到本地（离线可用，体积较大）。
    /// </summary>
    public async Task<ImportReport> EnsureAsync(bool force = false, bool allStandardImages = false,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CardImageDir);
        Directory.CreateDirectory(AppPaths.DataDir);

        // 1. PvE 卡牌数据（单文件，约 200KB；失败时用本地缓存）
        var dataDownloaded = await DownloadTpocDataAsync(progress, ct).ConfigureAwait(false);

        var pveCards = 0;
        if (File.Exists(TpocJsonPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(TpocJsonPath, ct).ConfigureAwait(false);
                pveCards = _cards.MergeCardsJson(json, forceCollectible: true);
                progress?.Report($"已导入 PvE 卡牌数据 {pveCards} 张");
            }
            catch (Exception ex)
            {
                progress?.Report($"PvE 数据解析失败：{ex.Message}");
            }
        }

        // 2. 98 系列统一走 tpoc/latest 单图地址（避免下载整包）
        NormalizePveImageUrls();
        var pveImages = await DownloadPveImagesAsync(progress, ct).ConfigureAwait(false);

        // 3. 全库名称/描述：官方缺失部分用游戏本地化文本补齐
        var filled = FillCardInfoFromLocalization();
        progress?.Report($"已补齐卡牌名称/描述 {filled} 处");

        // 4. dd_code 复用、同名补图
        var alias = FillByAlias();
        var sameName = FillBySameName();
        if (alias + sameName > 0)
            progress?.Report($"复用已有卡图 {alias} 张 / 同名补图 {sameName} 张");

        // 5. 可选：批量下载全部标准卡图
        var standardImages = allStandardImages
            ? await DownloadStandardImagesAsync(progress, ct).ConfigureAwait(false)
            : 0;

        _cards.SaveCache();

        var codes = AllGameCodes();
        var localImages = codes.Count(HasLocalImage);
        var networkImages = codes.Count(code =>
            _cards.TryGet(code, out var info) && !string.IsNullOrWhiteSpace(info.ImageUrl));
        var report = new ImportReport(_version, _locale, dataDownloaded, pveCards, codes.Count,
            localImages, pveImages, alias, sameName, standardImages, networkImages);
        progress?.Report($"卡牌资源就绪：本地 {localImages} 张 / 官方图按需下载 {networkImages} 张 / 共 {codes.Count} 张");
        return report;
    }

    // ---------------------------------------------------------------- tpoc 数据 / 98 系列卡图

    private async Task<bool> DownloadTpocDataAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var url = $"{LatestTpocRoot}/{_locale}/data/tpoc-{_locale}.json";
        try
        {
            progress?.Report("正在下载 PvE 卡牌数据（tpoc）…");
            var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            if (json.Length < 1024) return false;

            var tmp = TpocJsonPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, TpocJsonPath, true);
            return true;
        }
        catch
        {
            // 网络失败时保留旧缓存，下次启动再试
            return false;
        }
    }

    /// <summary>把 tpoc 卡图的版本号地址统一换成 latest（用户指定 98 系列使用 latest 单图）。</summary>
    private void NormalizePveImageUrls()
    {
        foreach (var card in _cards.AllCards)
        {
            if (card.ImageUrl.Contains("/tpoc/", StringComparison.OrdinalIgnoreCase))
                card.ImageUrl = PveImageUrl(card.Code);
        }
    }

    /// <summary>下载 98 系列官方卡图到本地（99 系列不下载，走本地拼图）。</summary>
    private async Task<int> DownloadPveImagesAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var codes = _cards.AllCards
            .Select(card => card.Code)
            .Concat(CardDatabase.ExtraCardCodes)
            .Where(code => code.StartsWith("98", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0) return 0;

        var todo = codes.Where(code => !HasLocalImage(code)).ToList();
        if (todo.Count == 0) return 0;

        progress?.Report($"正在下载 98 系列卡图 {todo.Count} 张…");
        var done = 0;
        await Parallel.ForEachAsync(todo,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (code, token) =>
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(PveImageUrl(code), token).ConfigureAwait(false);
                    if (bytes.Length < 1024) return;

                    var path = ImagePath(code);
                    var tmp = path + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, token).ConfigureAwait(false);
                    File.Move(tmp, path, true);
                    Interlocked.Increment(ref done);
                }
                catch
                {
                    // 个别卡没有官方图（403）时保持缺失，交给界面显示代码
                }
            }).ConfigureAwait(false);

        progress?.Report($"98 系列卡图已落地 {done}/{todo.Count} 张");
        return done;
    }

    // ---------------------------------------------------------------- 数据 / 卡图补齐

    private int FillCardInfoFromLocalization()
    {
        var additions = new List<CardInfo>();
        var changed = 0;

        foreach (var code in AllGameCodes())
        {
            var description = CleanLocText(_loc.Get($"card_{code}_Description"));
            var name = CleanLocText(_loc.GetCardName(code));

            if (_cards.TryGet(code, out var info) && info is not null)
            {
                if (string.IsNullOrWhiteSpace(info.Name) && name.Length > 0)
                {
                    info.Name = name;
                    changed++;
                }

                if (string.IsNullOrWhiteSpace(info.Description) && description.Length > 0)
                {
                    info.Description = description;
                    changed++;
                }

                continue;
            }

            var fallback = CardDatabase.TryGetFallback(code, out var extra) ? extra : null;
            additions.Add(new CardInfo
            {
                Code = code,
                Name = name.Length > 0 ? name : (fallback?.Name ?? code),
                Cost = fallback?.Cost ?? -1,
                Attack = fallback?.Attack,
                Health = fallback?.Health,
                Type = fallback?.Type ?? "",
                RegionRef = fallback?.RegionRef ?? "",
                RegionRefs = fallback?.RegionRefs?.ToList() ?? new List<string>(),
                Keywords = fallback?.Keywords?.ToList() ?? new List<string>(),
                KeywordRefs = fallback?.KeywordRefs?.ToList() ?? new List<string>(),
                Subtypes = fallback?.Subtypes?.ToList() ?? new List<string>(),
                SpellSpeed = fallback?.SpellSpeed ?? "",
                ImageUrl = fallback?.ImageUrl ?? "",
                DataDragonCode = fallback?.DataDragonCode ?? "",
                Description = description,
                Collectible = true
            });
        }

        _cards.UpsertCards(additions);

        // 第二遍：白板卡 / 教程卡 / 变体卡官方本就没有规则文本，
        // 先复用同名正式卡的描述，再用已有属性组成事实性说明，保证悬停提示不空。
        var nameToDescription = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _cards.AllCards)
        {
            var cardName = card.Name?.Trim();
            var cardDescription = card.Description?.Trim();
            if (!string.IsNullOrEmpty(cardName) && !string.IsNullOrEmpty(cardDescription) &&
                !nameToDescription.ContainsKey(cardName))
                nameToDescription[cardName] = cardDescription;
        }

        foreach (var code in AllGameCodes())
        {
            if (!_cards.TryGet(code, out var info) || info is null) continue;
            if (!string.IsNullOrWhiteSpace(info.Description)) continue;

            info.Description = !string.IsNullOrWhiteSpace(info.Name) &&
                               nameToDescription.TryGetValue(info.Name.Trim(), out var sameName)
                ? sameName
                : BuildFallbackDescription(info);
            changed++;
        }

        return changed + additions.Count;
    }

    /// <summary>官方没有规则文本时，用卡牌已有属性组成一行说明（不编造效果）。</summary>
    private static string BuildFallbackDescription(CardInfo info)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Type)) parts.Add(info.Type.Trim());
        if (!string.IsNullOrWhiteSpace(info.Region)) parts.Add(info.Region.Trim());
        else if (!string.IsNullOrWhiteSpace(info.RegionRef)) parts.Add(info.RegionRef.Trim());
        if (info.Cost >= 0) parts.Add($"費用 {info.Cost}");
        if (info.Attack is { } attack && info.Health is { } health) parts.Add($"{attack}/{health}");
        if (info.Keywords.Count > 0) parts.Add(string.Join(" / ", info.Keywords));
        return parts.Count > 0 ? string.Join(" · ", parts) : "遊戲內卡牌（官方無規則說明）";
    }

    /// <summary>游戏独占卡若已标注 dd_code，直接复用对应的标准卡本地图。</summary>
    private int FillByAlias()
    {
        var count = 0;
        foreach (var code in CardDatabase.ExtraCardCodes)
        {
            if (!CardDatabase.TryGetFallback(code, out var fallback) || fallback is null) continue;
            if (string.IsNullOrWhiteSpace(fallback.DataDragonCode)) continue;
            if (HasLocalImage(code)) continue;
            if (!HasLocalImage(fallback.DataDragonCode)) continue;

            try
            {
                File.Copy(ImagePath(fallback.DataDragonCode), ImagePath(code), true);
                count++;
            }
            catch
            {
                // ignore
            }
        }

        return count;
    }

    /// <summary>DD 未收录、但和某张已有卡同名的卡（教程/变体卡），复用同名卡图。</summary>
    private int FillBySameName()
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string code)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code)) return;
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = new List<string>();
            if (!list.Contains(code, StringComparer.OrdinalIgnoreCase)) list.Add(code);
        }

        foreach (var card in _cards.AllCards) Add(card.Name, card.Code);
        foreach (var (code, name) in _loc.GetCardNames()) Add(name, code);

        var count = 0;
        foreach (var code in AllGameCodes())
        {
            if (HasLocalImage(code)) continue;
            if (!_cards.TryGet(code, out var info) || string.IsNullOrWhiteSpace(info.Name)) continue;
            if (!byName.TryGetValue(info.Name, out var candidates)) continue;

            var source = candidates
                .Where(candidate => !string.Equals(candidate, code, StringComparison.OrdinalIgnoreCase)
                                    && HasLocalImage(candidate))
                .OrderBy(candidate => VariantRegex.IsMatch(candidate) ? 1 : 0)
                .ThenBy(candidate => candidate.Length)
                .FirstOrDefault();
            if (source is null) continue;

            try
            {
                File.Copy(ImagePath(source), ImagePath(code), true);
                count++;
            }
            catch
            {
                // ignore
            }
        }

        return count;
    }

    /// <summary>批量下载标准卡 / PvE 卡的官方卡图（可选，用于完全离线预览）。</summary>
    private async Task<int> DownloadStandardImagesAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var todo = _cards.AllCards
            .Where(card => !string.IsNullOrWhiteSpace(card.ImageUrl) && !HasLocalImage(card.Code))
            .Select(card => (card.Code, Url: ToHttps(card.ImageUrl)))
            .DistinctBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (todo.Count == 0) return 0;

        progress?.Report($"正在下载官方卡图 {todo.Count} 张…");
        var done = 0;
        await Parallel.ForEachAsync(todo,
            new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(item.Url, token).ConfigureAwait(false);
                    if (bytes.Length < 1024) return;
                    var tmp = ImagePath(item.Code) + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, token).ConfigureAwait(false);
                    File.Move(tmp, ImagePath(item.Code), true);
                    var current = Interlocked.Increment(ref done);
                    if (current % 100 == 0) progress?.Report($"官方卡图 {current}/{todo.Count}");
                }
                catch
                {
                    // 单张失败时保留缺失状态，悬停时还会再试
                }
            }).ConfigureAwait(false);

        return done;
    }

    // ---------------------------------------------------------------- 工具

    private HashSet<string> AllGameCodes()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in _cards.AllCodes)
            if (CardCodeRegex.IsMatch(code)) codes.Add(code.ToUpperInvariant());
        foreach (var code in CardDatabase.ExtraCardCodes)
            if (CardCodeRegex.IsMatch(code)) codes.Add(code.ToUpperInvariant());
        foreach (var (code, _) in _loc.GetCardNames())
            if (CardCodeRegex.IsMatch(code)) codes.Add(code.ToUpperInvariant());
        return codes;
    }

    private bool HasLocalImage(string code) => File.Exists(ImagePath(code));

    private string ImagePath(string code) => Path.Combine(CardImageDir, code + ".png");

    private static string ToHttps(string url) =>
        url.StartsWith("http://dd.b.pvp.net/", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["http://".Length..]
            : url;

    private static string CleanLocText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<nobr>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("</nobr>", "", StringComparison.OrdinalIgnoreCase);
        text = HtmlTagRegex.Replace(text, "");
        text = BracketRegex.Replace(text, "");
        text = ValueRegex.Replace(text, "$1");
        text = PlaceholderRegex.Replace(text, "");
        return text.Trim();
    }
}
