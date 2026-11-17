using System.Text.Json.Serialization;
using Brush = System.Windows.Media.Brush;

namespace LoRPoCTracker.Models;

/// <summary>Data Dragon 中的卡牌静态信息。</summary>
public sealed class CardInfo
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Cost { get; set; }

    /// <summary>费用文本；本地化兜底卡（无 Data Dragon 数据）费用未知时显示「-」。</summary>
    [JsonIgnore]
    public string CostText => Cost < 0 ? "-" : Cost.ToString();

    /// <summary>攻击力；法术/地标为 null。</summary>
    public int? Attack { get; set; }

    /// <summary>生命值；法术/地标为 null。</summary>
    public int? Health { get; set; }

    public string Type { get; set; } = "";
    public string SuperType { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string Region { get; set; } = "";
    public string RegionRef { get; set; } = "";

    /// <summary>全部地区（双地区卡有 2 个）。</summary>
    public List<string> RegionRefs { get; set; } = new();
    public string Description { get; set; } = "";
    public string FlavorText { get; set; } = "";

    /// <summary>英雄牌升级条件（Data Dragon levelupDescriptionRaw）。</summary>
    public string LevelUpDescription { get; set; } = "";

    /// <summary>法术速度（慢速/快速/疾速/技能）。</summary>
    public string SpellSpeed { get; set; } = "";

    /// <summary>卡牌特性/种族（如「菁英」「約德爾」），用于卡名下方的特性行。</summary>
    public List<string> Subtypes { get; set; } = new();

    public List<string> Keywords { get; set; } = new();

    /// <summary>关键词的英文标识（如 Overwhelm），用于匹配关键词图标。</summary>
    public List<string> KeywordRefs { get; set; } = new();

    /// <summary>特性/种族的英文标识（如 Yordle），用于顶部特性条图标。</summary>
    public List<string> SubtypeRefs { get; set; } = new();
    public string ImageUrl { get; set; } = "";

    /// <summary>游戏独占卡对应的 Data Dragon 卡代码；非空表示网络已有同一张卡，不需要本地拼图。</summary>
    public string DataDragonCode { get; set; } = "";

    /// <summary>是否为可收集卡；false 表示衍生物（如小蜘蛛），Boss 牌库不计入。</summary>
    public bool Collectible { get; set; } = true;
    public bool HasCollectibleField { get; set; }

    /// <summary>地区颜色条，供编辑窗口的搜索结果使用。</summary>
    [JsonIgnore]
    public Brush RegionBrush => RegionPalette.Get(RegionRef);
}
