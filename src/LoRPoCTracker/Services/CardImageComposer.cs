using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LoRPoCTracker.Config;
using LoRPoCTracker.Models;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace LoRPoCTracker.Services;

/// <summary>卡面关键词（中文文本 + 英文标识，用于匹配图标）。</summary>
public sealed record CardKeyword(string Text, string Ref);

/// <summary>拼图所需的卡牌数据。</summary>
public sealed record CardComposeData(
    string Code,
    string Name,
    int? Cost,
    int? Attack,
    int? Health,
    string Kind,
    string TopLabel,
    IReadOnlyList<string> RegionRefs,
    IReadOnlyList<CardKeyword> Keywords,
    string Description,
    string LevelUpDescription,
    string ArtPath,
    string? SpeedIconPath,
    string SpellSpeed);

/// <summary>
/// 卡牌拼图：用卡框模板（tools/frames）+ 本地原画 + 名称/费用/说明，
/// 在 .NET 内直接渲染成完整卡图并保存 PNG。
/// </summary>
public static class CardImageComposer
{
    private static readonly string FrameDir = Path.Combine(AppContext.BaseDirectory, "tools", "frames");

    /// <summary>游戏导出的卡框元素（类型条、地区托盘、关键词背景等）。</summary>
    private static readonly string ElementsDir = Path.Combine(
        AppPaths.DataDir, "local_assets", "elements", "cardframes", "shared");

    /// <summary>卡面右上角地区徽记。</summary>
    private static readonly string FactionIconDir = Path.Combine(
        AppPaths.DataDir, "local_assets", "elements", "faction_icons");

    /// <summary>关键词图标（按英文关键词名匹配，可选）。</summary>
    private static readonly string KeywordIconDir = Path.Combine(
        AppPaths.DataDir, "local_assets", "elements", "keyword_icons");

    /// <summary>随程序分发的小图标（游戏资源里没有的补充图标，如陷阱）。</summary>
    private static readonly string BundledIconDir = Path.Combine(
        AppContext.BaseDirectory, "tools", "icons");

    /// <summary>游戏插画取景参数（IllustrationProfile.RoiFull）：插画名 → (中心 x,y, 取景高度 z)。</summary>
    private static readonly string RoiPath = Path.Combine(
        AppPaths.DataDir, "local_assets", "illustration_roi.json");

    private static Dictionary<string, (double X, double Y, double Z)>? _roiIndex;

    /// <summary>重新从磁盘加载取景参数（首次提取 99 系列资源后调用）。</summary>
    public static void ResetRoiCache() => _roiIndex = null;

    private static readonly Dictionary<string, string> FrameFiles = new()
    {
        ["unit"] = "unit.png",
        ["champion"] = "champion.png",
        ["spell_slow"] = "spell_slow.png",
        ["spell_fast"] = "spell_fast.png",
        ["spell_burst"] = "spell_burst.png",
        ["spell_action"] = "spell_action.png",
        ["landmark"] = "landmark.png"
    };

    /// <summary>一套卡面布局参数（相对卡框宽/高，0~1；y 从上往下算）。</summary>
    private sealed record Layout(
        double CostX, double CostY, double CostSize,
        double PowerX, double HealthX, double StatY, double StatSize,
        double TopBarY, double TopBarW, double TopBarH, double TopBarText,
        double TrayCx, double TrayCy, double TrayW, double TrayH,
        double TrayIconSize, double TrayIconX,
        double TrayIconSingleY, double TrayIconMainY, double TrayIconExtraY,
        double SpeedIconX, double SpeedIconY, double SpeedIconSize,
        double SpeedPillCx, double SpeedPillGap, double SpeedPillIconSize,
        double NameTop, double TextOffsetY, double NameMaxSize, double NameKwGap, double TextWidth,
        double KeywordSize, double KwIconSize, double KwPadX, double KwGap, double KwBarH, double KwRowGap,
        double DescSize, double DescGap, double TextBottom, double TextBottomLandmark,
        double LevelUpSize, double LevelUpGap, double LevelUpTextGap,
        double PanelTop, double? PanelBottom);

    /// <summary>法术速度中文名（SpellSpeed 缺失时按卡框类型回退）。</summary>
    private static readonly Dictionary<string, string> SpeedLabels = new()
    {
        ["spell_slow"] = "慢速",
        ["spell_fast"] = "快速",
        ["spell_burst"] = "疾速",
        ["spell_action"] = "技能"
    };

    /// <summary>单位 / 英雄 / 地标。</summary>
    private static readonly Layout UnitLayout = new(
        CostX: 0.120, CostY: 0.080, CostSize: 0.066,
        PowerX: 0.124, HealthX: 0.876, StatY: 0.930, StatSize: 0.057,
        TopBarY: 0.049, TopBarW: 0.40, TopBarH: 0.047, TopBarText: 0.027,
        TrayCx: 0.909, TrayCy: 0.141, TrayW: 0.182, TrayH: 0.250,
        TrayIconSize: 0.137, TrayIconX: 0.55,
        TrayIconSingleY: 0.675, TrayIconMainY: 0.675, TrayIconExtraY: 0.3125,
        SpeedIconX: 0.095, SpeedIconY: 0.185, SpeedIconSize: 0.085,
        SpeedPillCx: 0.5, SpeedPillGap: 0.008, SpeedPillIconSize: 0.034,
        NameTop: 0.600, TextOffsetY: 0.0, NameMaxSize: 0.066, NameKwGap: 0.008, TextWidth: 0.812,
        KeywordSize: 0.033, KwIconSize: 0.034, KwPadX: 0.016, KwGap: 0.010, KwBarH: 0.048, KwRowGap: 0.004,
        DescSize: 0.045, DescGap: 0.014, TextBottom: 0.868, TextBottomLandmark: 0.893,
        LevelUpSize: 0.031, LevelUpGap: 0.012, LevelUpTextGap: 0.010,
        PanelTop: 0.400, PanelBottom: null);

    /// <summary>法术 / 技能 / 武器。</summary>
    private static readonly Layout SpellLayout = new(
        CostX: 0.124, CostY: 0.09, CostSize: 0.066,
        PowerX: 0.124, HealthX: 0.876, StatY: 0.947, StatSize: 0.057,
        TopBarY: 0.049, TopBarW: 0.40, TopBarH: 0.047, TopBarText: 0.027,
        TrayCx: 0.805, TrayCy: 0.122, TrayW: 0.273, TrayH: 0.188,
        TrayIconSize: 0.137, TrayIconX: 0.32,
        TrayIconSingleY: 0.675, TrayIconMainY: 0.675, TrayIconExtraY: 0.3125,
        SpeedIconX: 0.1, SpeedIconY: 0.230, SpeedIconSize: 0.095,
        SpeedPillCx: 0.5, SpeedPillGap: 0.008, SpeedPillIconSize: 0.068,
        NameTop: 0.600, TextOffsetY: 0.0, NameMaxSize: 0.066, NameKwGap: 0.008, TextWidth: 0.800,
        KeywordSize: 0.033, KwIconSize: 0.034, KwPadX: 0.016, KwGap: 0.010, KwBarH: 0.048, KwRowGap: 0.004,
        DescSize: 0.045, DescGap: 0.014, TextBottom: 0.930, TextBottomLandmark: 0.930,
        LevelUpSize: 0.031, LevelUpGap: 0.012, LevelUpTextGap: 0.010,
        PanelTop: 0.500, PanelBottom: 0.945);

    public static string FramePath(string kind) =>
        Path.Combine(FrameDir, FrameFiles.TryGetValue(kind, out var file) ? file : FrameFiles["unit"]);

    /// <summary>查找法术类型图标：优先本地资源，其次程序自带图标。</summary>
    public static string? ResolveSpeedIcon(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var local = Path.Combine(ElementsDir, fileName);
        if (File.Exists(local)) return local;

        var bundled = Path.Combine(BundledIconDir, fileName);
        return File.Exists(bundled) ? bundled : null;
    }

    /// <summary>按卡牌类型文字 + 法术速度选择卡框。</summary>
    public static string PickKind(string? typeText, string? spellSpeed = null)
    {
        var text = typeText ?? "";
        if (text.Contains("地標") || text.Contains("地标")) return "landmark";
        if (text.Contains("技能")) return "spell_action";
        if (text.Contains("法術") || text.Contains("法术"))
        {
            var speed = spellSpeed ?? "";
            if (speed.Contains("疾速") || speed.Contains("爆發") || speed.Contains("爆发"))
                return "spell_burst";
            if (speed.Contains("快速") || speed.Contains("專注") || speed.Contains("专注"))
                return "spell_fast";
            return "spell_slow";
        }
        if (text.Contains("英雄")) return "champion";
        if (text.Contains("單位") || text.Contains("单位")) return "unit";
        return "spell_slow";
    }

    /// <summary>生成一张完整卡图到 outPath。</summary>
    /// <summary>
    /// 纯色占位原画：官方与游戏都没有插画的内部卡（99NE/99NU 等）用它拼图，
    /// 保证这些卡也有可预览的卡面（卡名、费用、说明照常渲染）。
    /// </summary>
    public static string EnsurePlaceholderArt()
    {
        var path = Path.Combine(AppPaths.DataDir, "local_assets", "placeholder_art.png");
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromRgb(24, 34, 54), 0),
                    new(Color.FromRgb(58, 46, 82), 1)
                }
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, 1024, 1024));
        }

        var bitmap = new RenderTargetBitmap(1024, 1024, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    /// <summary>
    /// 卡面背景的区域色调：从 99 + 两位区域码解析区域（如 99DE、99NX、99PZ…），
    /// 按区域主色压暗后作为渐变起点；非 99 卡或未知区域沿用紫色。
    /// </summary>
    private static Color ResolveRegionTint(string code)
    {
        var regionRef = RegionPalette.RegionRefFromCode(code);
        if (regionRef is null) return Color.FromRgb(56, 48, 78);

        // PZ 保持现有的青色底
        if (string.Equals(regionRef, "PiltoverZaun", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromRgb(18, 74, 78);
        }

        var color = RegionPalette.GetColor(regionRef);
        return Color.FromRgb(
            (byte)Math.Clamp(color.R * 0.30 + 6, 8, 84),
            (byte)Math.Clamp(color.G * 0.30 + 6, 8, 84),
            (byte)Math.Clamp(color.B * 0.30 + 6, 8, 84));
    }

    public static void Compose(CardComposeData card, string outPath)
    {
        var frame = Load(Path.Combine(FrameDir,
            FrameFiles.TryGetValue(card.Kind, out var file) ? file : FrameFiles["unit"]));
        var art = Load(card.ArtPath);
        var icon = !string.IsNullOrWhiteSpace(card.SpeedIconPath) && File.Exists(card.SpeedIconPath)
            ? Load(card.SpeedIconPath)
            : null;
        var isSpell = card.Kind.StartsWith("spell", StringComparison.Ordinal);
        var isUnitLike = card.Kind is "unit" or "champion";

        var W = (double)frame.PixelWidth;
        var H = (double)frame.PixelHeight;

        var region = ResolveRegionTint(card.Code);

        // 单位/英雄/地标 与 法术/技能/武器 使用各自的布局参数
        var L = isSpell ? SpellLayout : UnitLayout;

        var inner = isSpell
            ? new Rect(W * 0.155, H * 0.035, W * 0.69, H * 0.49)
            : new Rect(W * 0.028, H * 0.02, W * 0.944, H * 0.96);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 深色底
            var bgBrush = new LinearGradientBrush(region, Color.FromRgb(10, 12, 18), 90);
            if (isSpell)
            {
                dc.PushClip(new EllipseGeometry(inner));
                dc.DrawRectangle(bgBrush, null, inner);
                DrawIllustration(dc, art, inner, card.Code);
                dc.Pop();
            }
            else
            {
                dc.DrawRectangle(bgBrush, null, inner);
                dc.PushClip(new RectangleGeometry(inner, 18, 18));
                DrawIllustration(dc, art, inner, card.Code);
                dc.Pop();
            }

            // 文字区渐变：顶部完全透明，向下渐深（看不出上边缘）
            var panelTop = H * L.PanelTop;
            var panelBottom = L.PanelBottom is { } panelRatio ? H * panelRatio : inner.Bottom;
            var panelRect = isSpell
                ? new Rect(W * 0.07, panelTop, W * 0.86, Math.Max(0, panelBottom - panelTop))
                : new Rect(inner.X, panelTop, inner.Width, Math.Max(0, panelBottom - panelTop));
            var fade = new LinearGradientBrush(
                Color.FromArgb(0, 8, 10, 14), Color.FromArgb(252, 8, 10, 14), 90);
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(150, 8, 10, 14), 0.45));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(232, 8, 10, 14), 0.78));
            dc.DrawRectangle(fade, null, panelRect);

            // 真实卡框
            dc.DrawImage(frame, new Rect(0, 0, W, H));

            // 顶部特性条（subtypes）
            DrawTopBar(dc, card.TopLabel, W, H, L);

            // 费用
            if (card.Cost is >= 0)
            {
                var costText = Text(card.Cost.Value.ToString(), H * L.CostSize, Colors.White, bold: true);
                dc.DrawText(costText, new Point(
                    W * L.CostX - costText.WidthIncludingTrailingWhitespace / 2,
                    H * L.CostY - costText.Height / 2));
            }

            // 攻击 / 生命
            if (isUnitLike)
            {
                if (card.Attack is >= 0)
                    DrawStat(dc, card.Attack.Value.ToString(), W * L.PowerX, H * L.StatY, H * L.StatSize);
                if (card.Health is >= 0)
                    DrawStat(dc, card.Health.Value.ToString(), W * L.HealthX, H * L.StatY, H * L.StatSize);
            }

            // 名称 → 关键词行 → 描述 → 升级条件
            // 单位/英雄：整块文字底对齐、自下往上排布（锚点=攻击/生命贴图上沿）
            // 法术/技能/地标：保持原来的自上而下排布（法术在名称下方多一行速度胶囊）
            var textWidth = W * L.TextWidth;
            var textLeft = (W - textWidth) / 2;
            var topLimit = H * (L.NameTop + L.TextOffsetY);
            var textBottomLimit = H * (card.Kind == "landmark" ? L.TextBottomLandmark : L.TextBottom);
            var nameText = FitText(card.Name, W * L.NameMaxSize, W * 0.040,
                textWidth, H * 0.075, bold: true, Color.FromRgb(250, 246, 232), wrap: false);

            // 法术速度（图标 + 名称）放在卡名下方，和关键词同款胶囊背景
            var speedLabel = string.IsNullOrWhiteSpace(card.SpellSpeed)
                ? SpeedLabels.GetValueOrDefault(card.Kind, "")
                : card.SpellSpeed;
            var speedItems = new List<(string Text, string? IconPath)>();
            if (isSpell && speedLabel.Length > 0) speedItems.Add((speedLabel, card.SpeedIconPath));
            // 速度胶囊按图标尺寸整体缩放（图标 / 文字 / 背景一起变大）
            var speedScale = L.KwIconSize > 0 ? L.SpeedPillIconSize / L.KwIconSize : 1.0;

            // 速度名称已由速度胶囊展示，避免关键词行重复
            var speedNames = new HashSet<string> { "慢速", "快速", "疾速", "專注", "专注", "技能", "陷阱" };
            var keywordItems = card.Keywords
                .Where(k => !isSpell || (!speedNames.Contains(k.Text) && k.Text != speedLabel))
                .Select(k => (Text: k.Text,
                    IconPath: string.IsNullOrWhiteSpace(k.Ref)
                        ? null
                        : Path.Combine(KeywordIconDir, k.Ref.ToLowerInvariant() + ".png")))
                .ToList();

            var hasDescription = !string.IsNullOrWhiteSpace(card.Description);
            var hasLevelUp = !string.IsNullOrWhiteSpace(card.LevelUpDescription);
            var levelText = hasLevelUp
                ? FitText("升級條件：" + card.LevelUpDescription, W * L.LevelUpSize, W * 0.024,
                    textWidth, H * 0.058, bold: true, Color.FromRgb(236, 198, 124))
                : null;
            var levelHeight = levelText is null
                ? 0
                : H * L.LevelUpGap + H * L.LevelUpTextGap + levelText.Height;

            FormattedText? descText = null;
            var descHeight = 0.0;
            double textTop;
            if (isUnitLike)
            {
                var fixedHeight = nameText.Height
                                  + (keywordItems.Count > 0 ? H * L.NameKwGap + H * L.KwBarH + H * L.KwRowGap : 0)
                                  + (hasDescription ? H * L.DescGap : 0)
                                  + levelHeight;
                if (hasDescription)
                {
                    descText = FitText(card.Description, W * L.DescSize, W * 0.028, textWidth,
                        Math.Max(H * 0.03, textBottomLimit - topLimit - fixedHeight),
                        bold: true, Color.FromRgb(234, 228, 212));
                    descHeight = descText.Height;
                }
                textTop = Math.Max(topLimit, textBottomLimit - fixedHeight - descHeight);
            }
            else
            {
                var rowsHeight = 0.0;
                if (speedItems.Count > 0)
                    rowsHeight += H * L.SpeedPillGap + H * L.KwBarH * speedScale + H * L.KwRowGap;
                if (keywordItems.Count > 0)
                    rowsHeight += (speedItems.Count == 0 ? H * L.NameKwGap : 0) + H * L.KwBarH + H * L.KwRowGap;
                if (hasDescription)
                {
                    descText = FitText(card.Description, W * L.DescSize, W * 0.028, textWidth,
                        Math.Max(H * 0.03,
                            textBottomLimit - levelHeight
                            - (topLimit + nameText.Height + rowsHeight + H * L.DescGap)),
                        bold: true, Color.FromRgb(234, 228, 212));
                    descHeight = descText.Height;
                }
                textTop = topLimit;
            }

            var y = textTop;
            dc.DrawText(nameText, new Point(textLeft + (textWidth - nameText.WidthIncludingTrailingWhitespace) / 2, y));
            y += nameText.Height;

            if (isUnitLike)
            {
                if (keywordItems.Count > 0)
                {
                    y += H * L.NameKwGap;
                    y = DrawPillRow(dc, keywordItems, W / 2, y, textWidth, W, H, L);
                }
            }
            else
            {
                if (speedItems.Count > 0)
                {
                    y += H * L.SpeedPillGap;
                    y = DrawPillRow(dc, speedItems, W * L.SpeedPillCx, y, textWidth, W, H, L,
                        W * L.SpeedPillIconSize);
                }
                if (keywordItems.Count > 0)
                {
                    if (speedItems.Count == 0) y += H * L.NameKwGap;
                    y = DrawPillRow(dc, keywordItems, W / 2, y, textWidth, W, H, L);
                }
            }

            if (descText is not null)
            {
                y += H * L.DescGap;
                dc.DrawText(descText, new Point(
                    textLeft + (textWidth - descText.WidthIncludingTrailingWhitespace) / 2, y));
                y += descHeight;
            }

            // 英雄升级条件（金色，位于描述下方）
            if (levelText is not null)
            {
                var lineY = isUnitLike
                    ? y + H * L.LevelUpGap
                    : Math.Min(y + H * L.LevelUpGap, textBottomLimit - H * 0.048);
                var lineBrush = new LinearGradientBrush(
                    Color.FromArgb(0, 214, 178, 110), Color.FromArgb(200, 226, 190, 128), 0);
                lineBrush.GradientStops.Insert(1,
                    new GradientStop(Color.FromArgb(200, 226, 190, 128), 0.5));
                lineBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 214, 178, 110), 1));
                dc.DrawRectangle(lineBrush, null, new Rect(W * 0.16, lineY, W * 0.68, Math.Max(1, H * 0.0016)));

                dc.DrawText(levelText, new Point(
                    textLeft + (textWidth - levelText.WidthIncludingTrailingWhitespace) / 2,
                    lineY + H * L.LevelUpTextGap));
            }

            // 法术速度图标（费用下方独立图标；位置/大小用 SpeedIconX/Y/Size 调）
            if (isSpell && icon is not null)
            {
                var trimmed = TrimTransparent(icon);
                var iconHeight = H * L.SpeedIconSize;
                var iconWidth = iconHeight * trimmed.PixelWidth / trimmed.PixelHeight;
                dc.DrawImage(trimmed, new Rect(W * L.SpeedIconX - iconWidth / 2,
                    H * L.SpeedIconY - iconHeight / 2, iconWidth, iconHeight));
            }

            // 右上角地区托盘 + 徽记
            DrawFactionTray(dc, card.RegionRefs, W, H, isSpell, L);
        }

        var bitmap = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outPath);
        encoder.Save(stream);
    }

    /// <summary>
    /// 按游戏 IllustrationProfile 的 RoiFull 取景绘制原画：
    /// ROI 中心 = (x, y) * 插画尺寸，取景高度 = z * 插画高度，宽度按卡面比例；
    /// 区域超出插画时贴边收缩，再等比 cover 铺满目标区域（不拉伸）。
    /// 找不到 ROI 时退回居中 cover。
    /// </summary>
    private static void DrawIllustration(DrawingContext dc, ImageSource art, Rect dest, string code)
    {
        var W = art.Width;
        var H = art.Height;
        double vw = W, vh = H, cx = W / 2, cy = H / 2;

        var roi = FindRoi(code);
        if (roi is { } r)
        {
            vh = Math.Min(H, Math.Max(H * 0.05, r.Z * H));
            vw = Math.Min(W, vh * (dest.Width / dest.Height));
            var left = Math.Clamp(r.X * W - vw / 2, 0, Math.Max(0, W - vw));
            var top = Math.Clamp(r.Y * H - vh / 2, 0, Math.Max(0, H - vh));
            cx = left + vw / 2;
            cy = top + vh / 2;
        }

        var scale = Math.Max(dest.Width / vw, dest.Height / vh);
        var rect = new Rect(
            dest.X + dest.Width / 2 - cx * scale,
            dest.Y + dest.Height / 2 - cy * scale,
            W * scale,
            H * scale);
        dc.PushClip(new RectangleGeometry(dest));
        dc.DrawImage(art, rect);
        dc.Pop();
    }

    /// <summary>按卡牌代码匹配插画 ROI（插画名包含卡牌代码，取最短的键）。</summary>
    public static (double X, double Y, double Z)? FindRoi(string code)
    {
        _roiIndex ??= LoadRoiIndex();
        if (_roiIndex.Count == 0) return null;

        var key = Squash(code);
        string? best = null;
        foreach (var candidate in _roiIndex.Keys)
        {
            if (!candidate.Contains(key, StringComparison.Ordinal)) continue;
            if (best is null || candidate.Length < best.Length) best = candidate;
        }

        return best is null ? null : _roiIndex[best];
    }

    private static Dictionary<string, (double X, double Y, double Z)> LoadRoiIndex()
    {
        var map = new Dictionary<string, (double, double, double)>();
        if (!File.Exists(RoiPath)) return map;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RoiPath));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var x = property.Value.TryGetProperty("x", out var xe) ? xe.GetDouble() : 0.5;
                var y = property.Value.TryGetProperty("y", out var ye) ? ye.GetDouble() : 0.5;
                var z = property.Value.TryGetProperty("z", out var ze) ? ze.GetDouble() : 1.0;
                map[property.Name] = (x, y, z);
            }
        }
        catch
        {
            // 索引损坏时退回居中裁切
        }

        return map;
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

    /// <summary>顶部特性条：游戏 UnitSubtypeTray（icontray_nomip 背景 + 特性文字）。</summary>
    private static void DrawTopBar(DrawingContext dc, string text, double W, double H, Layout L)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var tray = LoadOrNull(Path.Combine(ElementsDir, "icontray_nomip.png"));
        var centerY = H * L.TopBarY;
        if (tray is not null)
        {
            dc.DrawImage(tray,
                new Rect(W / 2 - W * L.TopBarW / 2, centerY - H * L.TopBarH / 2, W * L.TopBarW, H * L.TopBarH));
        }

        var label = FitText(text, W * L.TopBarText, W * 0.019,
            W * L.TopBarW * 0.78, H * (L.TopBarH * 0.8), bold: true, Color.FromRgb(240, 232, 214), wrap: false);
        dc.DrawText(label, new Point(W / 2 - label.WidthIncludingTrailingWhitespace / 2,
            centerY - label.Height / 2));
    }

    private static void DrawStat(DrawingContext dc, string value, double x, double y, double size)
    {
        var text = Text(value, size, Colors.White, bold: true);
        dc.DrawText(text, new Point(x - text.WidthIncludingTrailingWhitespace / 2, y - text.Height / 2));
    }

    /// <summary>
    /// 名称下方一行胶囊：每个「图标 + 文字」胶囊用游戏 Keyword_TextBackground 作背景。
    /// items 为 (文字, 图标绝对路径)；图标为空/不存在时只画文字。返回下一行的 y。
    /// iconSize 可覆盖胶囊内图标高：图标、文字、背景会按同一比例一起缩放。
    /// </summary>
    private static double DrawPillRow(DrawingContext dc, IReadOnlyList<(string Text, string? IconPath)> items,
        double centerX, double top, double maxWidth, double W, double H, Layout L, double? iconSize = null)
    {
        if (items.Count == 0) return top + H * L.DescGap;

        var defaultIcon = W * L.KwIconSize;
        var iconHeight = iconSize ?? defaultIcon;
        var scale = defaultIcon > 0 ? iconHeight / defaultIcon : 1.0;

        var bg = LoadOrNull(Path.Combine(ElementsDir, "keyword_textbackground.png"));
        var size = W * L.KeywordSize * scale;
        var minSize = W * 0.020 * scale;
        var gap = W * L.KwGap * scale;
        var padX = W * L.KwPadX * scale;
        var pillHeight = H * L.KwBarH * scale;

        // 图标先裁掉透明边，再按实际宽高比预留宽度（避免宽图标压到文字）
        var icons = items
            .Select(item => item.IconPath is { } iconPath && LoadOrNull(iconPath) is { } image
                ? TrimTransparent(image)
                : null)
            .ToList();
        var iconWidths = icons
            .Select(icon => icon is null ? 0 : iconHeight * icon.PixelWidth / icon.PixelHeight)
            .ToArray();

        double[] TextWidths(double s) => items
            .Select(k => Text(k.Text, s, Colors.White, bold: true).WidthIncludingTrailingWhitespace)
            .ToArray();

        // 逐个缩小字号，直到所有胶囊放进可用宽度
        var widths = TextWidths(size);
        double TotalWidth() => widths.Sum() + iconWidths.Sum()
                               + items.Count * (padX * 2 + gap * 0.4)
                               + gap * Math.Max(0, items.Count - 1);
        while (size > minSize && TotalWidth() > maxWidth)
        {
            size -= 0.5;
            widths = TextWidths(size);
        }

        var total = TotalWidth();
        var x = centerX - total / 2;
        var textY = top + (pillHeight - size * 1.35) / 2;

        for (var i = 0; i < items.Count; i++)
        {
            var textWidth = widths[i];
            var iconWidth = iconWidths[i];
            var pillWidth = padX * 2 + iconWidth + gap * 0.4 + textWidth;
            var rect = new Rect(x, top, pillWidth, pillHeight);
            if (bg is not null) dc.DrawImage(bg, rect);
            else dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(190, 14, 16, 22)), null, rect, 8, 8);

            // 图标（关键词图标或法术速度图标，先裁掉透明边）
            if (icons[i] is { } trimmed)
            {
                dc.DrawImage(trimmed, new Rect(x + padX, top + (pillHeight - iconHeight) / 2,
                    iconWidth, iconHeight));
            }

            var text = Text(items[i].Text, size, Color.FromRgb(238, 220, 170), bold: true);
            dc.DrawText(text, new Point(x + padX + iconWidth + gap * 0.4, textY));

            x += pillWidth + gap;
        }

        return top + pillHeight + H * L.KwRowGap;
    }

    /// <summary>右上角地区托盘（单/双地区）+ 地区徽记。</summary>
    private static void DrawFactionTray(DrawingContext dc, IReadOnlyList<string> regionRefs, double W, double H,
        bool isSpell, Layout L)
    {
        var refs = regionRefs.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Take(2).ToList();
        if (refs.Count == 0) return;

        var trayName = isSpell
            ? (refs.Count > 1 ? "factiontraybasic_two.png" : "factiontraybasic_one.png")
            : (refs.Count > 1 ? "factiontray_basic_two.png" : "factiontray_basic_one.png");
        var tray = LoadOrNull(Path.Combine(ElementsDir, "factiontray", trayName));

        var cx = W * L.TrayCx;
        var cy = H * L.TrayCy;
        var tw = W * L.TrayW;
        var th = H * L.TrayH;
        var trayRect = new Rect(cx - tw / 2, cy - th / 2, tw, th);
        if (tray is not null) dc.DrawImage(tray, trayRect);

        var iconH = W * L.TrayIconSize;
        var iconCx = trayRect.X + tw * L.TrayIconX;
        void DrawIcon(string regionRef, double centerYRatio)
        {
            var icon = LoadOrNull(Path.Combine(FactionIconDir, "icon-" + regionRef.ToLowerInvariant() + ".png"));
            if (icon is null) return;
            var iconW = iconH * icon.PixelWidth / icon.PixelHeight;
            var iy = trayRect.Bottom - th * centerYRatio - iconH / 2;
            dc.DrawImage(icon, new Rect(iconCx - iconW / 2, iy, iconW, iconH));
        }

        if (refs.Count > 1)
        {
            DrawIcon(refs[0], L.TrayIconMainY);
            DrawIcon(refs[1], L.TrayIconExtraY);
        }
        else
        {
            DrawIcon(refs[0], L.TrayIconSingleY);
        }
    }

    private static FormattedText Text(string text, double size, Color color, bool bold = false,
        double maxWidth = 4096, double maxHeight = 4096)
    {
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"),
            FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(color), 96)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = maxHeight,
            Trimming = TextTrimming.CharacterEllipsis
        };
    }

    /// <summary>自动缩小字号，直到文字放进给定区域（wrap=false 时保持单行）。</summary>
    private static FormattedText FitText(string text, double startSize, double minSize,
        double maxWidth, double maxHeight, bool bold, Color color, bool wrap = true)
    {
        var size = Math.Max(minSize, startSize);
        while (true)
        {
            var formatted = wrap
                ? Text(text, size, color, bold, maxWidth, maxHeight)
                : Text(text, size, color, bold);
            var fits = formatted.WidthIncludingTrailingWhitespace <= maxWidth + 0.5 &&
                       formatted.Height <= maxHeight + 0.5;
            if (fits || size <= minSize) return formatted;
            size -= 1;
        }
    }

    /// <summary>裁掉图像四周的透明边（速度图标等小图对齐用）。</summary>
    private static BitmapSource TrimTransparent(BitmapSource source)
    {
        try
        {
            var w = source.PixelWidth;
            var h = source.PixelHeight;
            var stride = w * 4;
            var pixels = new byte[stride * h];
            source.CopyPixels(pixels, stride, 0);

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (var y = 0; y < h; y++)
            {
                var row = y * stride;
                for (var x = 0; x < w; x++)
                {
                    if (pixels[row + x * 4 + 3] <= 8) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY) return source;
            return Crop(source, minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        catch
        {
            return source;
        }
    }

    private static CroppedBitmap Crop(BitmapSource source, int x, int y, int w, int h)
    {
        var rect = new Int32Rect(
            Math.Max(0, Math.Min(x, source.PixelWidth - 1)),
            Math.Max(0, Math.Min(y, source.PixelHeight - 1)),
            Math.Max(1, Math.Min(w, source.PixelWidth - x)),
            Math.Max(1, Math.Min(h, source.PixelHeight - y)));
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }

    private static BitmapSource? LoadOrNull(string path) => File.Exists(path) ? Load(path) : null;

    private static BitmapSource Load(string path)
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
}
