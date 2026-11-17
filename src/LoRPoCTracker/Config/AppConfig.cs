using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoRPoCTracker.Models;

namespace LoRPoCTracker.Config;

/// <summary>应用配置，保存于 %LOCALAPPDATA%\LoRPoCTracker\config.json。</summary>
public sealed class AppConfig
{
    public int ApiPort { get; set; } = 21337;

    /// <summary>Data Dragon 语言，跟随客户端语言使用 zh_tw。</summary>
    public string Locale { get; set; } = "zh_tw";

    /// <summary>LoR 安装目录（留空则自动从进程/常见路径检测）。</summary>
    public string? GameDir { get; set; }

    public int PollIntervalMs { get; set; } = 500;

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 332;
    public double Height { get; set; } = 640;

    public double Opacity { get; set; } = 0.94;

    /// <summary>点击穿透，开启后鼠标事件直接落到游戏。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>自动贴在游戏窗口右侧。</summary>
    public bool AutoDock { get; set; } = true;

    /// <summary>隐藏已经打空（剩余 0 张）的牌。</summary>
    public bool HideExhausted { get; set; }

    /// <summary>悬停/编辑时显示卡牌图片（首次显示时按需下载到本地）。</summary>
    public bool ShowCardImages { get; set; }

    public bool ShowOpponentPlayed { get; set; } = true;

    /// <summary>进入英雄之路大地图后，显示「本地图 Boss 牌库」下拉（含 Boss 头像）。</summary>
    public bool ShowMapBossDecks { get; set; } = true;

    /// <summary>「本地图 Boss 牌库」中排除本局已经打过的 Boss（日志里的战斗对手）。</summary>
    public bool HideDefeatedMapBosses { get; set; } = true;

    /// <summary>按 Boss 英雄卡自动录制各关卡出牌，用于推算剩余牌库（仅管理员版生效）。</summary>
    public bool RecordBossDecks { get; set; } = true;

    /// <summary>
    /// 软件版本：false = 普通版（只记牌、只读使用已有录制），
    /// true = 管理员版（显示录制、编辑、管理选项并允许写入牌库数据）。
    /// </summary>
    public bool AdminMode { get; set; }

    /// <summary>牌库数据源：本地源固定存在（唯一写入目标），其余为只读网络源。</summary>
    public List<DataSourceConfig> Sources { get; set; } = CreateDefaultSources();

    /// <summary>当前使用的数据源 Id（默认 jsDelivr 云端只读源）。</summary>
    public string ActiveSourceId { get; set; } = "jsdelivr";

    /// <summary>退出前是否弹出确认；勾选「下次不再提示」后置为 false。</summary>
    public bool ConfirmExit { get; set; } = true;

    /// <summary>启动时后台检查在线更新（静默，发现新版本才提示）。</summary>
    public bool CheckUpdateOnStartup { get; set; } = true;

    /// <summary>已忽略更新的版本号（该版本不再自动提示）。</summary>
    public string? IgnoredUpdateVersion { get; set; }

    /// <summary>云端牌库数据仓库：jsDelivr 与 GitHub raw 共用同一份文件。</summary>
    private const string CloudRepo = "Nohtaehp/lor-poc-decks";
    private const string CloudBranch = "main";

    private static string JsDelivrUrl =>
        $"https://cdn.jsdelivr.net/gh/{CloudRepo}@{CloudBranch}/data/encounters.json";

    private static string GitHubRawUrl =>
        $"https://raw.githubusercontent.com/{CloudRepo}/{CloudBranch}/data/encounters.json";

    /// <summary>默认源：本地（固定可写）+ jsDelivr（默认）+ GitHub raw（备用）。</summary>
    private static List<DataSourceConfig> CreateDefaultSources() => new()
    {
        new DataSourceConfig { Id = DataSourceConfig.LocalId, Name = "本地录制", IsLocal = true },
        new DataSourceConfig
        {
            Id = "jsdelivr",
            Name = "jsDelivr（默认云源）",
            Files = new List<string> { JsDelivrUrl }
        },
        new DataSourceConfig
        {
            Id = "github",
            Name = "GitHub 仓库（备用）",
            Files = new List<string> { GitHubRawUrl }
        }
    };

    [JsonIgnore]
    public string ConfigPath => Path.Combine(AppPaths.DataDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "config.json");
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时回退到默认值
        }

        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 忽略保存失败（只影响下次启动位置）
        }
    }

    private void Normalize()
    {
        if (ApiPort <= 0 || ApiPort > 65535) ApiPort = 21337;
        if (PollIntervalMs < 200) PollIntervalMs = 500;
        if (Width < 240) Width = 332;
        if (Height < 300) Height = 640;
        if (Opacity < 0.3 || Opacity > 1) Opacity = 0.94;
        if (string.IsNullOrWhiteSpace(Locale)) Locale = "zh_tw";
        NormalizeSources();
    }

    private void NormalizeSources()
    {
        Sources = (Sources ?? new List<DataSourceConfig>())
            .Where(source => source is not null)
            .ToList();

        // 未填写文件地址的空网络源不显示（含旧版本留下的空模板；已配置地址的源保留）
        Sources.RemoveAll(source => !source.IsLocal && (source.Files is null || source.Files.Count == 0));

        var local = Sources.FirstOrDefault(source =>
            source.IsLocal || source.Id == DataSourceConfig.LocalId);
        if (local is null)
        {
            local = new DataSourceConfig
            {
                Id = DataSourceConfig.LocalId,
                Name = "本地录制",
                IsLocal = true
            };
            Sources.Insert(0, local);
        }

        local.Id = DataSourceConfig.LocalId;
        local.IsLocal = true;
        local.Name = "本地录制";
        local.Files = new List<string>();

        foreach (var source in Sources.Where(source => !source.IsLocal))
        {
            if (string.IsNullOrWhiteSpace(source.Id) || source.Id == DataSourceConfig.LocalId)
                source.Id = Guid.NewGuid().ToString("N")[..8];
            if (string.IsNullOrWhiteSpace(source.Name)) source.Name = "网络源";
            source.Files = (source.Files ?? new List<string>())
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Select(file => file.Trim())
                .ToList();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources)
        {
            if (!seen.Add(source.Id)) source.Id = Guid.NewGuid().ToString("N")[..8];
        }

        if (string.IsNullOrWhiteSpace(ActiveSourceId) ||
            Sources.All(source => source.Id != ActiveSourceId))
            ActiveSourceId = DataSourceConfig.LocalId;
    }
}

public static class AppPaths
{
    /// <summary>程序运行目录下的 data 子目录：配置、录制、卡牌缓存与本地素材都在这里。</summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>卡牌静态数据缓存（v3 起包含攻击/生命/特性/升级条件/地区）。</summary>
    public static string CardCachePath(string locale) => Path.Combine(DataDir, $"cards_v3_{locale}.json");

    public static string LogPath => Path.Combine(DataDir, "tracker.log");
}
