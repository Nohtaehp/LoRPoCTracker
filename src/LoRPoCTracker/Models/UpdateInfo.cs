using System.Text.Json.Serialization;

namespace LoRPoCTracker.Models;

/// <summary>
/// 在线更新清单（仓库 update/manifest.json）。
/// 版本号用整数比较（1.2.3 -> 10203），URL 均为相对仓库的路径占位。
/// </summary>
public sealed class UpdateInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("versionCode")]
    public int VersionCode { get; set; }

    [JsonPropertyName("published")]
    public string Published { get; set; } = "";

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    [JsonPropertyName("package")]
    public string Package { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("minRuntime")]
    public string MinRuntime { get; set; } = "9.0";

    /// <summary>把 "1.2.3" 解析为可比较整数；失败返回 0。</summary>
    public static int ParseVersionCode(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.TrimStart('v', 'V').Split('.');
        if (parts.Length == 0 || parts.Length > 4) return 0;

        var code = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number) || number < 0 || number > 999) return 0;
            code = code * 1000 + number;
        }

        for (var i = parts.Length; i < 3; i++) code *= 1000;
        return code;
    }
}
