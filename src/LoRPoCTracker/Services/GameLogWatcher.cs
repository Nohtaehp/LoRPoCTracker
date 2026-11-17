using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Timer = System.Threading.Timer;

namespace LoRPoCTracker.Services;

/// <summary>
/// 只读跟随 LoR 客户端日志，解析当前冒险与节点：
///   adventureConfigGuid=xxx   （冒险/地图，稳定）
///   travel to node N          （地图节点序号）
/// 用于把 Boss 牌库录制精确到"哪张地图的哪个节点"。
/// </summary>
public sealed class GameLogWatcher : IDisposable
{
    private static readonly Regex AdventureRegex =
        new(@"adventureConfigGuid=([0-9a-fA-F\-]{36})", RegexOptions.Compiled);

    private static readonly Regex PlayableRegex =
        new(@"adventurePlayableGuid=([0-9a-fA-F\-]{36})", RegexOptions.Compiled);

    private static readonly Regex NodeRegex =
        new(@"travel to node (\d+)", RegexOptions.Compiled);

    /// <summary>本次冒险（run）的唯一 id：adventure/v1/run/{guid}/node/{n}/travel。</summary>
    private static readonly Regex RunRegex =
        new(@"/run/([0-9a-fA-F\-]{36})/", RegexOptions.Compiled);

    /// <summary>实际日志格式：POST http://.../adventure/v1/run/{run}/node/{n}/travel。</summary>
    private static readonly Regex NodeTravelRegex =
        new(@"/node/(\d+)/travel", RegexOptions.Compiled);

    private static readonly Regex EnemyNameRegex =
        new(@"OnPlayerAdded: PlayerID=PlayerTwo, Name=(\S+)", RegexOptions.Compiled);

    // 启动时回读日志的量：中途打开记牌器时也要能还原「当前是否在大地图」（
    // 大地图的 Opening 行可能出现在较早的位置，2MB 常常不够）。
    private const int SeedBytes = 16 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Timer _timer;
    private FileStream? _stream;
    private string? _path;
    private string _remainder = "";

    public GameLogWatcher()
    {
        _timer = new Timer(_ => Poll(), null, 1200, 1200);
    }

    public string? AdventureGuid { get; private set; }
    public string? PlayableGuid { get; private set; }
    public int? NodeIndex { get; private set; }

    /// <summary>本次冒险（run）id；变化说明开始了新的英雄之路 run。</summary>
    public string? RunId { get; private set; }

    private readonly object _stateGate = new();
    private readonly HashSet<string> _runBosses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本局遇到过的 Boss（战斗中出现过的对手名称 key）。
    /// 用于在地图 Boss 牌库列表里排除「本局已经打过」的节点。
    /// </summary>
    public IReadOnlyCollection<string> GetRunBosses()
    {
        lock (_stateGate) return _runBosses.ToArray();
    }

    /// <summary>
    /// 当前是否停留在英雄之路大地图（bacon://Adventure 场景）。
    /// 进入：日志出现 "Opening URI ... bacon://Adventure?adventureConfigGuid=..."；
    /// 离开：出现 "Firing PreUnload for current location BaconURI [bacon://Adventure..."（进战斗/回主菜单）。
    /// </summary>
    public bool IsInAdventureMap { get; private set; }

    /// <summary>对手名称的本地化 key（如 Villain_Adventure_SH_Boss_Name / card_03MT018_Name）。</summary>
    public string? EnemyNameKey { get; private set; }

    public string? CurrentLogPath => _path;
    public DateTime LastReadUtc { get; private set; }

    private void Poll()
    {
        lock (_gate)
        {
            try
            {
                var path = FindNewestLog();
                if (path is null) return;

                if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) || _stream is null)
                {
                    Attach(path);
                }

                if (_stream is null) return;
                if (_stream.Length < _stream.Position)
                {
                    // 文件被截断/轮转，重新附加
                    Attach(path);
                }

                ReadNew();
            }
            catch
            {
                // 日志读取失败不影响记牌
            }
        }
    }

    private void Attach(string path)
    {
        _stream?.Dispose();
        _stream = null;
        _remainder = "";
        _path = path;

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        _stream = stream;

        var seedStart = Math.Max(0, stream.Length - SeedBytes);
        if (seedStart > 0)
        {
            // 从行首开始，避免截断半行
            stream.Seek(seedStart, SeekOrigin.Begin);
            var probe = new byte[4096];
            var read = stream.Read(probe, 0, probe.Length);
            var newline = Array.IndexOf(probe, (byte)'\n', 0, read);
            seedStart += newline >= 0 ? newline + 1 : 0;
        }

        stream.Seek(seedStart, SeekOrigin.Begin);
        using var seed = new MemoryStream();
        stream.CopyTo(seed);
        ApplyText(Encoding.UTF8.GetString(seed.ToArray()));

        stream.Seek(0, SeekOrigin.End);
    }

    private void ReadNew()
    {
        if (_stream is null) return;

        using var buffer = new MemoryStream();
        _stream.CopyTo(buffer);
        if (buffer.Length == 0) return;

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        ApplyText(text);
    }

    private void ApplyText(string text)
    {
        var combined = _remainder + text;
        var lines = combined.Split('\n');
        _remainder = lines.Length > 0 ? lines[^1] : "";

        for (var i = 0; i < lines.Length - 1; i++)
        {
            ApplyLine(lines[i]);
        }
    }

    private void ApplyLine(string line)
    {
        if (line.Length == 0) return;

        var match = AdventureRegex.Match(line);
        if (match.Success) AdventureGuid = match.Groups[1].Value;

        // 大地图场景的进入 / 离开（大地图 → 战斗、回主菜单）
        if (line.Contains("bacon://Adventure", StringComparison.Ordinal))
        {
            if (line.Contains("Firing PreUnload for current location", StringComparison.Ordinal))
            {
                IsInAdventureMap = false;
            }
            else if (match.Success &&
                     (line.Contains("Opening URI", StringComparison.Ordinal) ||
                      line.Contains("Requested to Open URI", StringComparison.Ordinal)))
            {
                IsInAdventureMap = true;
            }
        }

        // 冒险 run（大地图的"这一局"）：换 run 就重置本局已打过的 Boss
        var runMatch = RunRegex.Match(line);
        if (runMatch.Success)
        {
            var runId = runMatch.Groups[1].Value;
            if (!string.Equals(runId, RunId, StringComparison.OrdinalIgnoreCase))
            {
                RunId = runId;
                lock (_stateGate) _runBosses.Clear();
            }
        }

        match = PlayableRegex.Match(line);
        if (match.Success) PlayableGuid = match.Groups[1].Value;

        match = NodeRegex.Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var node))
        {
            NodeIndex = node;
        }

        // 实际日志是 HTTP 请求行：/adventure/v1/run/{run}/node/{n}/travel
        var travelMatch = NodeTravelRegex.Match(line);
        if (travelMatch.Success && int.TryParse(travelMatch.Groups[1].Value, out var travelNode))
        {
            NodeIndex = travelNode;
        }

        match = EnemyNameRegex.Match(line);
        if (match.Success)
        {
            EnemyNameKey = match.Groups[1].Value;
            lock (_stateGate) _runBosses.Add(EnemyNameKey);
        }

        LastReadUtc = DateTime.UtcNow;
    }

    private static string? FindNewestLog()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LOR_TRACKER_LOG_DIR");
        var roots = !string.IsNullOrWhiteSpace(overrideDir)
            ? new[] { overrideDir }
            : new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Riot Games", "Legends of Runeterra", "Logs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Riot Games", "符文大地傳說", "Logs")
            };

        string? newest = null;
        var newestTime = DateTime.MinValue;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "lor_client_*.log"))
            {
                var time = File.GetLastWriteTimeUtc(file);
                if (time > newestTime)
                {
                    newestTime = time;
                    newest = file;
                }
            }
        }

        return newest;
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
