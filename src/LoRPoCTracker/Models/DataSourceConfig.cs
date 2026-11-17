using System.Text.Json.Serialization;

namespace LoRPoCTracker.Models;

/// <summary>
/// 牌库录制数据源。本地源固定存在（id=local，data/encounters.json），是管理员录制/编辑的唯一写入目标；
/// 网络源只读，可配置多个 encounters.json 格式的直链文件，加载后合并成一个视图。
/// </summary>
public sealed class DataSourceConfig
{
    public const string LocalId = "local";

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>本地源固定 true，不可添加、不可删除。</summary>
    public bool IsLocal { get; set; }

    /// <summary>该源包含的录制文件（网络源为 http/https 直链，一行一个）。</summary>
    public List<string> Files { get; set; } = new();

    [JsonIgnore]
    public int FileCount => Files?.Count ?? 0;

    [JsonIgnore]
    public bool HasFiles => (Files ?? new List<string>()).Any(file => !string.IsNullOrWhiteSpace(file));

    [JsonIgnore]
    public string Summary => IsLocal
        ? "本地 · 可录制/编辑"
        : HasFiles ? $"只读 · {FileCount} 个文件" : "只读 · 未配置";
}
