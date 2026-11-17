using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using LoRPoCTracker.Config;
using Texture2DDecoder;
using TextureFormat = AssetsTools.NET.Texture.TextureFormat;

namespace LoRPoCTracker.Services;

/// <summary>99 系列资源提取结果。</summary>
public sealed record GameAssetResult(
    bool Extracted,
    int Illustrations,
    int Elements,
    int FactionIcons,
    string Message);

/// <summary>
/// 99 系列卡图所需的本地资源，全部从游戏安装目录现场生成：
///   - 卡框元素：特性条 / 关键词背景 / 地区托盘 / 法术速度图标
///   - 99 系列原画与取景参数
///   - 地区徽记
///
/// 结果写到 local_assets/，并写 99_assets.json 标记，后续启动不再重复处理。
/// 不依赖任何 Python 脚本或预生成资源。
/// </summary>
public sealed class GameAssetExtractor
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static readonly string[] RegionRefs =
    {
        "Runeterra", "BandleCity", "Bilgewater", "Demacia", "Freljord", "Ionia", "Noxus",
        "PiltoverZaun", "ShadowIsles", "Shurima", "Targon", "SpiritBlossom"
    };

    /// <summary>卡框元素：资源包内路径 → local_assets 下的相对路径。</summary>
    private static readonly (string Asset, string Relative)[] ElementAssets =
    {
        ("assets/art/cards/cardframes/shared/icontray_nomip.png",
            "elements/cardframes/shared/icontray_nomip.png"),
        ("assets/art/cards/cardframes/shared/keyword_textbackground.png",
            "elements/cardframes/shared/keyword_textbackground.png"),
        ("assets/art/cards/cardframes/shared/factiontray/factiontray_basic_one.png",
            "elements/cardframes/shared/factiontray/factiontray_basic_one.png"),
        ("assets/art/cards/cardframes/shared/factiontray/factiontray_basic_two.png",
            "elements/cardframes/shared/factiontray/factiontray_basic_two.png"),
        ("assets/art/cards/cardframes/shared/factiontray/factiontraybasic_one.png",
            "elements/cardframes/shared/factiontray/factiontraybasic_one.png"),
        ("assets/art/cards/cardframes/shared/factiontray/factiontraybasic_two.png",
            "elements/cardframes/shared/factiontray/factiontraybasic_two.png"),
        ("assets/art/cards/cardframes/shared/icon_slow_crispmip.png",
            "elements/cardframes/shared/icon_slow_crispmip.png"),
        ("assets/art/cards/cardframes/shared/icon_fast_crispmip.png",
            "elements/cardframes/shared/icon_fast_crispmip.png"),
        ("assets/art/cards/cardframes/shared/icon_burst_crispmip.png",
            "elements/cardframes/shared/icon_burst_crispmip.png"),
        ("assets/art/cards/cardframes/shared/icon_focus_crispmip.png",
            "elements/cardframes/shared/icon_focus_crispmip.png"),
        ("assets/art/cards/cardframes/shared/icon_skill_crispmip.png",
            "elements/cardframes/shared/icon_skill_crispmip.png"),
        ("assets/art/cards/cardframes/shared/icon_trap_crispmip.png",
            "elements/cardframes/shared/icon_trap_crispmip.png")
    };

    private readonly string? _configuredGameDir;
    private readonly string _locale;

    public GameAssetExtractor(string? configuredGameDir, string locale)
    {
        _configuredGameDir = configuredGameDir;
        _locale = string.IsNullOrWhiteSpace(locale) ? "zh_tw" : locale;
    }

    private static string LocalAssetsDir => Path.Combine(AppPaths.DataDir, "local_assets");
    private static string FactionIconDir => Path.Combine(LocalAssetsDir, "elements", "faction_icons");
    private static string IllustrationDir => Path.Combine(LocalAssetsDir, "illustrations");
    private static string RoiPath => Path.Combine(LocalAssetsDir, "illustration_roi.json");
    private static string MarkerPath => Path.Combine(LocalAssetsDir, "99_assets.json");

    /// <summary>
    /// 首次启动时把 99 系列所需资源生成到本地；资源已齐全时直接跳过。
    /// 返回本次是否真的执行了生成，以及原画/元素/阵营图标数量。
    /// </summary>
    public async Task<GameAssetResult> EnsureAsync(IReadOnlyCollection<string> cardCodes,
        IProgress<string>? progress = null, bool force = false, CancellationToken ct = default)
    {
        var gameDir = GamePaths.DetectGameDir(_configuredGameDir);
        if (gameDir is null)
            return new GameAssetResult(false, 0, 0, 0, "未找到 LoR 安装目录，99 系列使用占位原画");

        var assetRoot = Path.Combine(gameDir, "PatcherData", "PatchableFiles", "AssetBundles");
        var mapPath = Path.Combine(assetRoot, "AssetToBundleMap.txt");
        if (!File.Exists(mapPath))
            return new GameAssetResult(false, 0, 0, 0, "未找到 AssetToBundleMap.txt，无法提取 99 系列资源");

        var codes = cardCodes
            .Where(code => code.StartsWith("99", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
            return new GameAssetResult(false, 0, 0, 0, "没有需要拼图的 99 系列卡牌");

        progress?.Report("正在读取游戏资源索引…");
        var map = await Task.Run(() => ParseAssetMap(mapPath), ct).ConfigureAwait(false);
        if (map.Count == 0)
            return new GameAssetResult(false, 0, 0, 0, "游戏资源索引为空");

        var assetHash = ReadAssetHash(assetRoot);
        var profiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            var profileAsset = ResolveProfileAsset(code, map);
            if (profileAsset is not null) profiles[code] = profileAsset;
        }

        var profileCodes = profiles.Keys.ToList();

        var elementsReady = ElementAssets.All(item =>
            File.Exists(Path.Combine(LocalAssetsDir, item.Relative.Replace('/', Path.DirectorySeparatorChar))));
        var illustrationsReady = profileCodes.All(code =>
            File.Exists(Path.Combine(IllustrationDir, code + ".png")));

        if (!force && elementsReady && illustrationsReady && IsMarkerCurrent(assetHash) && File.Exists(RoiPath))
        {
            var marker = ReadMarker();
            return new GameAssetResult(false, marker?.Illustrations ?? profileCodes.Count,
                marker?.Elements ?? ElementAssets.Length, marker?.FactionIcons ?? 0,
                "99 系列资源已就绪");
        }

        Directory.CreateDirectory(LocalAssetsDir);
        Directory.CreateDirectory(Path.Combine(LocalAssetsDir, "elements", "cardframes", "shared"));
        Directory.CreateDirectory(FactionIconDir);
        Directory.CreateDirectory(IllustrationDir);

        var manager = new AssetsManager();
        var elementCount = 0;
        var illustrationCount = 0;
        var roi = new Dictionary<string, (double X, double Y, double Z)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 1. 卡框元素（特性条 / 关键词背景 / 地区托盘 / 速度图标）
            progress?.Report("正在从游戏提取 99 系列卡框元素…");
            foreach (var group in ElementAssets.GroupBy(item => map.GetValueOrDefault(item.Asset) ?? ""))
            {
                if (string.IsNullOrWhiteSpace(group.Key)) continue;
                var bundlePath = Path.Combine(assetRoot, group.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(bundlePath)) continue;

                if (!TryLoadBundle(manager, bundlePath, out var instance)) continue;
                try
                {
                    foreach (var item in group)
                    {
                        var target = Path.Combine(LocalAssetsDir,
                            item.Relative.Replace('/', Path.DirectorySeparatorChar));
                        var texture = FindTexture(manager, instance,
                            Path.GetFileNameWithoutExtension(item.Asset));
                        if (texture is null) continue;
                        if (SaveTexture(manager, instance, texture, target)) elementCount++;
                    }
                }
                finally
                {
                    manager.UnloadAll(true);
                }
            }

            // 2. 99 系列原画 + RoiFull（按 profile 资源分组，同一 bundle 只加载一次）
            var profileGroups = profileCodes
                .Select(code => (Code: code, Bundle: map[profiles[code]], Asset: profiles[code]))
                .GroupBy(item => item.Bundle, StringComparer.OrdinalIgnoreCase);
            var done = 0;
            foreach (var group in profileGroups)
            {
                ct.ThrowIfCancellationRequested();
                var bundlePath = Path.Combine(assetRoot, group.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(bundlePath)) continue;

                if (!TryLoadBundle(manager, bundlePath, out var instance)) continue;
                try
                {
                    foreach (var (code, _, profileAsset) in group)
                    {
                        if (TryExtractIllustration(manager, instance, code, profileAsset, out var roiValue))
                        {
                            illustrationCount++;
                            if (roiValue is { } value) roi[code.ToLowerInvariant()] = value;
                        }

                        done++;
                        if (done % 10 == 0)
                            progress?.Report($"正在提取 99 系列原画 {done}/{profileCodes.Count}");
                    }
                }
                finally
                {
                    manager.UnloadAll(true);
                }
            }
        }
        finally
        {
            manager.UnloadAll(true);
        }

        // 3. 地区徽记：Data Dragon core 小图（16×16 128px）
        var factionCount = 0;
        foreach (var region in RegionRefs)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(FactionIconDir, "icon-" + region.ToLowerInvariant() + ".png");
            await EnsureFactionIconAsync(region, target, ct).ConfigureAwait(false);
            if (File.Exists(target)) factionCount++;
        }

        // 4. 写 ROI 与标记（下次启动直接跳过）
        WriteRoi(roi);
        WriteMarker(gameDir, assetHash, illustrationCount, elementCount, factionCount);

        progress?.Report($"99 系列资源就绪：原画 {illustrationCount} 张 / 卡框元素 {elementCount} 个 / 地区徽记 {factionCount} 个");
        return new GameAssetResult(true, illustrationCount, elementCount, factionCount, "已从游戏提取 99 系列资源");
    }

    /// <summary>
    /// 按需生成任意卡牌的原始插画，Boss 头像复用同一来源。
    /// 已有本地插画（local_assets/illustrations/{CODE}.png）的卡直接跳过。
    /// 返回本次新生成的数量；游戏目录/索引不可用时返回 0。
    /// </summary>
    public static async Task<int> ExtractIllustrationsAsync(string? configuredGameDir,
        IReadOnlyCollection<string> codes, CancellationToken ct = default)
    {
        var gameDir = GamePaths.DetectGameDir(configuredGameDir);
        if (gameDir is null) return 0;

        var assetRoot = Path.Combine(gameDir, "PatcherData", "PatchableFiles", "AssetBundles");
        var mapPath = Path.Combine(assetRoot, "AssetToBundleMap.txt");
        if (!File.Exists(mapPath)) return 0;

        var missing = codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(code => !File.Exists(Path.Combine(IllustrationDir, code.ToUpperInvariant() + ".png")))
            .ToList();
        if (missing.Count == 0) return 0;

        var map = await Task.Run(() => ParseAssetMap(mapPath), ct).ConfigureAwait(false);
        if (map.Count == 0) return 0;

        var profiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in missing)
        {
            var profileAsset = ResolveProfileAsset(code, map);
            if (profileAsset is not null) profiles[code] = profileAsset;
        }

        if (profiles.Count == 0) return 0;
        Directory.CreateDirectory(IllustrationDir);

        var roi = new Dictionary<string, (double X, double Y, double Z)>(StringComparer.OrdinalIgnoreCase);
        var extracted = 0;
        var manager = new AssetsManager();

        try
        {
            // 同一 bundle 只加载一次，遍历资源时一次匹配该 bundle 内的所有目标卡
            foreach (var group in profiles.GroupBy(pair => map[pair.Value], StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                var bundlePath = Path.Combine(assetRoot, group.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(bundlePath)) continue;
                if (!TryLoadBundle(manager, bundlePath, out var instance)) continue;

                try
                {
                    var pending = group
                        .Select(pair => (Code: pair.Key, Asset: pair.Value,
                            ProfileName: Path.GetFileNameWithoutExtension(pair.Value)))
                        .ToList();

                    foreach (var info in instance.file.AssetInfos)
                    {
                        if (pending.Count == 0) break;
                        if (info.TypeId != 114) continue;

                        var field = manager.GetBaseField(instance, info);
                        if (field is null) continue;

                        var name = ReadString(field, "m_Name");
                        if (name.Length == 0) continue;

                        var index = pending.FindIndex(item => MatchesProfileName(name, item.Code, item.ProfileName));
                        if (index < 0) continue;

                        var (code, _, _) = pending[index];
                        pending.RemoveAt(index);

                        if (!TryExtractFromProfileField(manager, instance, field, code, out var roiValue)) continue;
                        extracted++;
                        if (roiValue is { } value) roi[code.ToLowerInvariant()] = value;
                    }
                }
                finally
                {
                    manager.UnloadAll(true);
                }
            }
        }
        finally
        {
            manager.UnloadAll(true);
        }

        if (roi.Count > 0) WriteRoi(roi);
        return extracted;
    }

    // ---------------------------------------------------------------- 资源生成

    private static string ProfileAssetPath(string code) =>
        $"assets/assetlibrary/illustrationprofiles/{code.ToLowerInvariant()}.asset";

    /// <summary>
    /// 找卡牌对应的 IllustrationProfile。多数卡是 {code}.asset，
    /// 少数卡带版本后缀（如 99ne056-v2.asset），按前缀匹配。
    /// </summary>
    private static string? ResolveProfileAsset(string code, Dictionary<string, string> map)
    {
        var exact = ProfileAssetPath(code);
        if (map.ContainsKey(exact)) return exact;

        // 变体后缀：99ne056-v2.asset、05bw005rq_nami_cleric.asset 等；
        // 紧跟数字的通常是另一张卡（代码末尾数字），不匹配。
        var prefix = $"assets/assetlibrary/illustrationprofiles/{code.ToLowerInvariant()}";
        return map.Keys
            .Where(key => key.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) &&
                          key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                          (key.Length == prefix.Length ||
                           key[prefix.Length] is '-' or '.' or '_' ||
                           char.IsLetter(key[prefix.Length])))
            .OrderBy(key => key.Length)
            .FirstOrDefault();
    }

    private static Dictionary<string, string> ParseAssetMap(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bundle = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(".bbq", StringComparison.OrdinalIgnoreCase))
            {
                bundle = line;
                continue;
            }

            if (bundle.Length > 0) map[line] = bundle;
        }

        return map;
    }

    private static bool TryLoadBundle(AssetsManager manager, string bundlePath, out AssetsFileInstance instance)
    {
        instance = null!;
        try
        {
            var bundle = manager.LoadBundleFile(bundlePath, false);
            instance = manager.LoadAssetsFileFromBundle(bundle, 0, true);
            return instance.file.AssetInfos.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static AssetFileInfo? FindTexture(AssetsManager manager, AssetsFileInstance instance, string targetName)
    {
        var targetSquash = Squash(targetName);
        AssetFileInfo? squashed = null;

        foreach (var info in instance.file.AssetInfos)
        {
            if (info.TypeId is not (28 or 213)) continue;
            var field = manager.GetBaseField(instance, info);
            if (field is null) continue;

            var name = ReadString(field, "m_Name");
            if (name.Length == 0) continue;

            // 先按原始文件名精确匹配：FactionTray_Basic_One / FactionTrayBasic_One
            // 去空格后同名，但其实是单位/法术两套不同托盘。
            if (name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            {
                if (info.TypeId == 28) return info;
                return ResolveSpriteTexture(manager, instance, info);
            }

            if (squashed is null && Squash(name) == targetSquash)
                squashed = info;
        }

        return squashed is null ? null : ResolveSpriteTexture(manager, instance, squashed);
    }

    private static AssetFileInfo? ResolveSpriteTexture(AssetsManager manager, AssetsFileInstance instance,
        AssetFileInfo info)
    {
        if (info.TypeId == 28) return info;

        // Sprite：用 m_RD.texture 的 PathID 找实际 Texture2D
        var spriteField = manager.GetBaseField(instance, info);
        var texture = spriteField?["m_RD"]?["texture"];
        if (texture is null) return null;
        var pathId = texture["m_PathID"].AsLong;
        return instance.file.AssetInfos.FirstOrDefault(asset => asset.PathId == pathId);
    }

    private static bool TryExtractIllustration(AssetsManager manager, AssetsFileInstance instance,
        string code, string profileAsset, out (double X, double Y, double Z)? roi)
    {
        roi = null;
        AssetTypeValueField? profileField = null;
        var profileName = Path.GetFileNameWithoutExtension(profileAsset);

        foreach (var info in instance.file.AssetInfos)
        {
            if (info.TypeId != 114) continue;
            var field = manager.GetBaseField(instance, info);
            if (field is null) continue;
            var name = ReadString(field, "m_Name");
            if (!MatchesProfileName(name, code, profileName)) continue;
            profileField = field;
            break;
        }

        if (profileField is null) return false;
        return TryExtractFromProfileField(manager, instance, profileField, code, out roi);
    }

    /// <summary>从已定位的 IllustrationProfile 字段保存原画与取景参数。</summary>
    private static bool TryExtractFromProfileField(AssetsManager manager, AssetsFileInstance instance,
        AssetTypeValueField profileField, string code, out (double X, double Y, double Z)? roi)
    {
        roi = null;

        var illustration = profileField["CardIllustration"];
        if (illustration is null) return false;

        var pathId = illustration["m_PathID"].AsLong;
        var textureInfo = instance.file.AssetInfos.FirstOrDefault(info => info.PathId == pathId && info.TypeId == 28);
        if (textureInfo is null) return false;

        var roiField = profileField["RoiFull"];
        if (roiField is not null)
        {
            roi = (roiField["x"].AsFloat, roiField["y"].AsFloat, roiField["z"].AsFloat);
        }

        var target = Path.Combine(IllustrationDir, code.ToUpperInvariant() + ".png");
        return SaveTexture(manager, instance, textureInfo, target);
    }

    private static bool MatchesProfileName(string name, string code, string profileName)
    {
        if (name.Equals(code, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(profileName, StringComparison.OrdinalIgnoreCase)) return true;
        return name.StartsWith(profileName + "-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SaveTexture(AssetsManager manager, AssetsFileInstance instance,
        AssetFileInfo info, string targetPath)
    {
        try
        {
            var field = manager.GetBaseField(instance, info);
            if (field is null) return false;

            var texture = TextureFile.ReadTextureFile(field);
            if (texture.m_Width <= 0 || texture.m_Height <= 0) return false;

            var data = texture.FillPictureData(instance);
            if (data is null || data.Length == 0) return false;

            var rgba = Decode(data, (int)texture.m_TextureFormat, texture.m_Width, texture.m_Height);
            if (rgba is null) return false;

            SavePng(targetPath, rgba, texture.m_Width, texture.m_Height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? Decode(byte[] data, int format, int width, int height)
    {
        var size = width * height * 4;
        try
        {
            switch (format)
            {
                case 28: // DXT1 Crunched
                {
                    var dxt = TextureDecoder.UnpackUnityCrunch(data);
                    if (dxt is null) return null;
                    var rgba = new byte[size];
                    return TextureDecoder.DecodeDXT1(dxt, width, height, rgba) ? rgba : null;
                }
                case 29: // DXT5 Crunched
                {
                    var dxt = TextureDecoder.UnpackUnityCrunch(data);
                    if (dxt is null) return null;
                    var rgba = new byte[size];
                    return TextureDecoder.DecodeDXT5(dxt, width, height, rgba) ? rgba : null;
                }
                case 10: // DXT1
                {
                    var rgba = new byte[size];
                    return TextureDecoder.DecodeDXT1(data, width, height, rgba) ? rgba : null;
                }
                case 12: // DXT5
                {
                    var rgba = new byte[size];
                    return TextureDecoder.DecodeDXT5(data, width, height, rgba) ? rgba : null;
                }
                case 4: // RGBA32
                    return data.Length >= size ? data[..size] : null;
                default:
                    return TextureFile.DecodeManagedData(data, (TextureFormat)format, width, height, false, null!);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Unity 纹理自下而上存储，保存时翻转成正常方向；同时 RGBA → BGRA。</summary>
    private static void SavePng(string path, byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        var bgra = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var source = y * stride;
            var target = (height - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                var s = source + x * 4;
                var t = target + x * 4;
                bgra[t] = rgba[s + 2];
                bgra[t + 1] = rgba[s + 1];
                bgra[t + 2] = rgba[s];
                bgra[t + 3] = rgba[s + 3];
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string ReadString(AssetTypeValueField field, string name)
    {
        var value = field[name];
        return value is null || value.IsDummy ? "" : value.AsString;
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

    // ---------------------------------------------------------------- 地区徽记 / 标记

    private async Task<bool> EnsureFactionIconAsync(string region, string target, CancellationToken ct)
    {
        try
        {
            if (File.Exists(target) && new FileInfo(target).Length > 1024) return false;

            var url = $"https://dd.b.pvp.net/latest/core/{_locale}/img/regions/icon-{region.ToLowerInvariant()}.png";
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length < 1024) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, target, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadAssetHash(string assetRoot)
    {
        try
        {
            var path = Path.Combine(assetRoot, "asset_bundle_manifest.json");
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("clientAssetHash", out var hash)
                ? hash.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private sealed record RoiEntry(double X, double Y, double Z);

    private static void WriteRoi(Dictionary<string, (double X, double Y, double Z)> roi)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var payload = new Dictionary<string, RoiEntry>(StringComparer.OrdinalIgnoreCase);

            // 保留磁盘上已有的取景参数（其他游戏独占卡可能仍在使用），只覆盖 99 系列条目
            if (File.Exists(RoiPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Dictionary<string, RoiEntry>>(
                        File.ReadAllText(RoiPath), options);
                    if (existing is not null)
                    {
                        foreach (var pair in existing) payload[pair.Key] = pair.Value;
                    }
                }
                catch
                {
                    // 旧文件损坏时直接写新表
                }
            }

            foreach (var pair in roi)
                payload[pair.Key] = new RoiEntry(pair.Value.X, pair.Value.Y, pair.Value.Z);

            Directory.CreateDirectory(Path.GetDirectoryName(RoiPath)!);
            File.WriteAllText(RoiPath, JsonSerializer.Serialize(payload, options));
        }
        catch
        {
            // ROI 写失败时拼图退回居中裁切
        }
    }

    private sealed record Marker(string Source, string AssetHash, string ExtractedUtc,
        int Illustrations, int Elements, int FactionIcons);

    private static Marker? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMarkerCurrent(string assetHash)
    {
        var marker = ReadMarker();
        return marker is not null &&
               string.Equals(marker.AssetHash, assetHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMarker(string gameDir, string assetHash, int illustrations, int elements,
        int factionIcons)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            var marker = new Marker(gameDir, assetHash,
                DateTime.UtcNow.ToString("o"), illustrations, elements, factionIcons);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 标记写失败只影响下次是否重复提取
        }
    }
}
