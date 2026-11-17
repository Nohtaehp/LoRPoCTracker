using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoRPoCTracker.Models;

namespace LoRPoCTracker.Services;

/// <summary>
/// 在线更新：从 GitHub 仓库读取更新清单，多源回退下载发布包，校验 SHA256 后安装。
///
/// 国内访问策略（按优先级自动尝试，任一成功即止）：
///   1. jsDelivr CDN（国内可直连，manifest 带时间戳绕过缓存）
///   2. GitHub raw
///   3. GitHub 官方 releases 下载
///   4. 公开加速镜像（ghfast.top / gh-proxy.com / ghproxy.net）
/// 只读取公开仓库的静态文件，不依赖任何第三方账号或接口。
/// </summary>
public sealed class UpdateService
{
    /// <summary>改成你自己的仓库：owner/repo 与分支。</summary>
    public const string RepoOwner = "Nohtaehp";
    public const string RepoName = "LoRPoCTracker";
    public const string RepoBranch = "main";

    /// <summary>Gitee 镜像仓库（国内优先源）。仓库需公开，否则自动回退到 GitHub 源。</summary>
    public const string GiteeOwner = "Nohtaehp";
    public const string GiteeRepo = "LoRPoCTracker";
    public const string GiteeBranch = "main";

    private const string ManifestPath = "update/manifest.json";
    private const string AppExeName = "LoRPoCTracker.exe";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>当前程序版本（由 GitHub Actions 在构建时通过 AssemblyVersion 写入）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var version = typeof(UpdateService).Assembly.GetName().Version;
            return version is null
                ? "0.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private static string RawManifestUrl =>
        $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{RepoBranch}/{ManifestPath}";

    private static string GiteeRawManifestUrl =>
        $"https://gitee.com/{GiteeOwner}/{GiteeRepo}/raw/{GiteeBranch}/{ManifestPath}";

    private static string ReleaseUrl(string fileName) =>
        $"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/{fileName}";

    private static string[] ManifestUrls()
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return
        [
            $"{GiteeRawManifestUrl}?t={stamp}",
            $"https://cdn.jsdelivr.net/gh/{RepoOwner}/{RepoName}@{RepoBranch}/{ManifestPath}?t={stamp}",
            RawManifestUrl,
            $"https://ghfast.top/{RawManifestUrl}",
            $"https://gh-proxy.com/{RawManifestUrl}"
        ];
    }

    private static string[] PackageUrls(UpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Package)) return [];
        var direct = ReleaseUrl(info.Package);
        var gitee =
            $"https://gitee.com/{GiteeOwner}/{GiteeRepo}/releases/download/v{info.Version}/{info.Package}";
        return
        [
            gitee,
            direct,
            $"https://ghfast.top/{direct}",
            $"https://gh-proxy.com/{direct}",
            $"https://ghproxy.net/{direct}"
        ];
    }

    /// <summary>
    /// 并行读取所有源，返回版本号最高的有效清单。
    /// jsDelivr 对 @branch 的缓存最长 12 小时且忽略查询参数，若“第一个成功即返回”
    /// 会拿到过期版本而漏掉更新，因此必须比较所有源后取最高版本。
    /// </summary>
    public async Task<(UpdateInfo? Info, string Source, string Error)> CheckAsync(
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var errors = new List<string>();

        var tasks = ManifestUrls().Select(url => FetchAsync(http, url, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        UpdateInfo? best = null;
        var bestSource = "";

        foreach (var (url, info, error) in results)
        {
            if (!string.IsNullOrEmpty(error))
            {
                errors.Add($"{SourceName(url)}: {error}");
                continue;
            }

            if (info is null || string.IsNullOrWhiteSpace(info.Package) ||
                UpdateInfo.ParseVersionCode(info.Version) <= 0)
            {
                errors.Add($"{SourceName(url)}: 清单格式无效");
                continue;
            }

            if (best is null ||
                UpdateInfo.ParseVersionCode(info.Version) > UpdateInfo.ParseVersionCode(best.Version))
            {
                best = info;
                bestSource = SourceName(url);
            }
        }

        return best is null
            ? (null, "", string.Join("；", errors))
            : (best, bestSource, "");
    }

    private static async Task<(string Url, UpdateInfo? Info, string Error)> FetchAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            return (url, JsonSerializer.Deserialize<UpdateInfo>(json, JsonOptions), "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户主动取消才向上抛出；HttpClient 超时属于该源失败，继续使用其他源
            throw;
        }
        catch (Exception ex)
        {
            return (url, null, ex.Message);
        }
    }

    /// <summary>判断清单版本是否比当前程序更新。</summary>
    public static bool IsNewer(UpdateInfo info)
    {
        var remote = info.VersionCode > 0 ? info.VersionCode : UpdateInfo.ParseVersionCode(info.Version);
        var local = UpdateInfo.ParseVersionCode(CurrentVersion);
        // 本地开发版（0.0.0）不提示更新
        return local > 0 && remote > local;
    }

    /// <summary>
    /// 多源下载发布包并校验 SHA256。返回本地 zip 路径。
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<string>? progress,
        CancellationToken ct = default)
    {
        var urls = PackageUrls(info);
        if (urls.Length == 0) throw new InvalidOperationException("更新清单未指定安装包");

        var tempDir = Path.Combine(Path.GetTempPath(), "LoRPoCTracker-update");
        Directory.CreateDirectory(tempDir);
        var target = Path.Combine(tempDir, info.Package);

        // 安装包约 70MB，慢速网络下 5 分钟容易超时
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var errors = new List<string>();

        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            var tmp = target + ".part";
            try
            {
                progress?.Report($"正在从 {SourceName(url)} 下载更新包…");
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                           .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using (var remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var local = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await remote.CopyToAsync(local, ct).ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrWhiteSpace(info.Sha256))
                {
                    progress?.Report("正在校验安装包完整性…");
                    var actual = await Sha256FileAsync(tmp).ConfigureAwait(false);
                    if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"{SourceName(url)}: 校验值不匹配");
                        TryDelete(tmp);
                        continue;
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                progress?.Report("更新包下载完成");
                return target;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{SourceName(url)}: {ex.Message}");
                TryDelete(tmp);
            }
        }

        throw new IOException("所有下载源均失败：" + string.Join("；", errors));
    }

    /// <summary>
    /// 写更新脚本并启动：等待本程序退出 → 用 zip 覆盖程序目录 → 重启。
    /// 脚本放在临时目录，避免被自身覆盖。
    /// </summary>
    public static void InstallAndRestart(string zipPath)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var script = Path.Combine(Path.GetTempPath(), $"lor-updater-{Guid.NewGuid():N}.ps1");
        var exe = Path.Combine(appDir, AppExeName);
        var pid = Environment.ProcessId;

        static string Ps(string? s) => "'" + (s ?? "").Replace("'", "''") + "'";

        var lines = new List<string>
        {
            $"$appDir = {Ps(appDir)}",
            $"$zip = {Ps(zipPath)}",
            $"$exe = {Ps(exe)}",
            $"$targetPid = {pid}",
            "$deadline = (Get-Date).AddSeconds(60)",
            "while ((Get-Process -Id $targetPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }",
            "try {",
            "  Expand-Archive -LiteralPath $zip -DestinationPath $appDir -Force -ErrorAction Stop",
            "  Start-Process -FilePath $exe",
            "} catch {",
            "  Set-Content -LiteralPath (Join-Path $env:TEMP 'lor-update-failed.txt') -Value $_.Exception.Message",
            "  if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe }",
            "}",
            "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue"
        };

        File.WriteAllText(script, string.Join("\r\n", lines), Encoding.UTF8);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static async Task<string> Sha256FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string SourceName(string url)
    {
        if (url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase)) return "Gitee 镜像";
        if (url.Contains("jsdelivr.net", StringComparison.OrdinalIgnoreCase)) return "jsDelivr CDN";
        if (url.Contains("ghfast.top", StringComparison.OrdinalIgnoreCase)) return "ghfast 镜像";
        if (url.Contains("gh-proxy.com", StringComparison.OrdinalIgnoreCase)) return "gh-proxy 镜像";
        if (url.Contains("ghproxy.net", StringComparison.OrdinalIgnoreCase)) return "ghproxy 镜像";
        if (url.Contains("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return "GitHub raw";
        return "GitHub";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
