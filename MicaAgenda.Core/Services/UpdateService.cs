using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.App.Services;

/// <summary>一个可下载的发布产物。</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long SizeBytes);

/// <summary>检查更新的结果。<see cref="Succeeded"/> 为 false 表示没查成（断网 / GitHub 拒绝等），不是「已是最新」。</summary>
public sealed record UpdateCheckResult(
    bool Succeeded,
    bool UpdateAvailable,
    Version? LatestVersion,
    string? TagName,
    UpdateAsset? Asset,
    string Message);

/// <summary>
/// 从 GitHub Releases 检查新版本、下载安装包，并把「装完自动重启」串起来。
///
/// 只依赖 BCL（HttpClient + System.Text.Json），所以放在 Core 里；宿主负责弹窗与退出程序。
/// 版本比较与产物挑选是纯函数，便于单测。
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepoOwner = "bubu-LZY";
    public const string RepoName = "desktop_todo_Calendar";

    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private readonly HttpClient _http;
    private readonly Func<Version> _currentVersionProvider;

    public UpdateService(Func<Version>? currentVersionProvider = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub API 不带 User-Agent 会直接 403；Accept 固定到 v3 语义，避免将来默认值漂移。
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", RepoName + "-updater");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        _currentVersionProvider = currentVersionProvider ?? CurrentAppVersion;
    }

    /// <summary>当前程序版本。</summary>
    public Version CurrentVersion => _currentVersionProvider();

    /// <summary>当前版本号的展示文本，如 v4.1.1。</summary>
    public string CurrentVersionText => "v" + Normalize(CurrentVersion);

    /// <summary>下载到的安装包放在这里（临时目录，每次检查更新会先清掉旧的）。</summary>
    public static string UpdateDirectory => Path.Combine(Path.GetTempPath(), "MicaAgendaUpdate");

    /// <summary>
    /// 当前程序版本，取自程序集。单一版本号来源是仓库根目录的 Directory.Build.props（&lt;Version&gt;），
    /// 由 SDK 派生到 AssemblyInformationalVersion，所以不用在这里再抄一份字面量。
    /// </summary>
    public static Version CurrentAppVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink 会追加 "+<commit>" 后缀，版本号部分在它之前
            var plus = informational.IndexOf('+');
            var text = plus >= 0 ? informational[..plus] : informational;
            if (Version.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    /// <summary>把 "v4.1.1" / "4.1.1" 解析成 Version；解析不出来返回 null。</summary>
    public static Version? ParseTagVersion(string? tag)
    {
        var text = (tag ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // 允许 "4.1.1-beta.1" 这类预发布后缀：只取前面的数字部分
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            text = text[..dash];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>版本号规范化成 x.y.z 三段，避免 4.1 与 4.1.0 显示不一致。</summary>
    public static string Normalize(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>
    /// 从发布产物里挑出当前平台该下载哪一个。
    ///
    /// Windows 只认安装包（desktop_todo_Calendar-Setup-*.exe）——绿色包 / 源码包都不能「静默安装」；
    /// macOS 按架构认 .dmg；Linux 认 .AppImage。
    /// </summary>
    public static UpdateAsset? SelectAsset(
        IReadOnlyList<UpdateAsset> assets,
        OSPlatform platform,
        Architecture architecture)
    {
        if (assets.Count == 0)
        {
            return null;
        }

        if (platform == OSPlatform.Windows)
        {
            return Find(assets, name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                        && name.Contains("-Setup-", StringComparison.OrdinalIgnoreCase));
        }

        if (platform == OSPlatform.OSX)
        {
            var suffix = architecture == Architecture.Arm64 ? "-arm64.dmg" : "-x64.dmg";
            return Find(assets, name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        if (platform == OSPlatform.Linux)
        {
            return Find(assets, name => name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));
        }

        return null;

        static UpdateAsset? Find(IReadOnlyList<UpdateAsset> list, Func<string, bool> match)
        {
            foreach (var asset in list)
            {
                if (match(asset.Name))
                {
                    return asset;
                }
            }

            return null;
        }
    }

    /// <summary>查询 GitHub 上的最新正式版，并和当前版本比一比。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseApi, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    false, false, null, null, null,
                    $"检查更新失败：GitHub 返回 {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var latest = ParseTagVersion(tag);
            if (latest is null)
            {
                return new UpdateCheckResult(false, false, null, tag, null, "检查更新失败：发布版本号无法识别");
            }

            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in assetsElement.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    var url = item.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value) ? value : 0L;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                    {
                        assets.Add(new UpdateAsset(name!, url!, size));
                    }
                }
            }

            var current = CurrentVersion;
            if (latest <= current)
            {
                return new UpdateCheckResult(
                    true, false, latest, tag, null,
                    $"已是最新版本（{CurrentVersionText}）");
            }

            var asset = SelectAsset(assets, CurrentPlatform(), RuntimeInformation.ProcessArchitecture);
            if (asset is null)
            {
                return new UpdateCheckResult(
                    true, true, latest, tag, null,
                    $"发现新版本 v{Normalize(latest)}，但这一版没有适配当前平台的安装包");
            }

            return new UpdateCheckResult(
                true, true, latest, tag, asset,
                $"发现新版本 v{Normalize(latest)}");
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, false, null, null, null, "检查更新已取消");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "UpdateService.Check");
            return new UpdateCheckResult(false, false, null, null, null, $"检查更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 下载安装包到临时目录，返回本地路径。已存在且大小一致时直接复用（重试 / 二次点击不用重下）。
    /// <paramref name="progress"/> 收到 0~1 的进度。
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(UpdateDirectory);
        var target = Path.Combine(UpdateDirectory, asset.Name);

        if (File.Exists(target) && asset.SizeBytes > 0 && new FileInfo(target).Length == asset.SizeBytes)
        {
            progress?.Report(1);
            return target;
        }

        // 先下到 .part 再改名：中途失败 / 被杀掉时不会留下一个「看起来下好了」的半截安装包
        var partial = target + ".part";
        try
        {
            using var response = await _http.GetAsync(
                asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? asset.SizeBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(partial))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Clamp((double)received / total, 0, 1));
                    }
                }
            }

            File.Move(partial, target, overwrite: true);
            progress?.Report(1);
            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// 静默安装刚下载的安装包，并让程序在装完后自动回来。
    ///
    /// 不能直接 StartProcess(安装包) 了事：setup.iss 在安装前会 taskkill 掉本进程，
    /// 进程一死就没人负责「装完再启动」。所以交给一个独立的 cmd —— 它先等安装程序结束，
    /// 再 start 本程序。cmd 不在安装程序的清理名单里，能一直活到安装结束。
    /// </summary>
    public static void LaunchInstallerAndRestart(string installerPath, string appExecutablePath)
    {
        Directory.CreateDirectory(UpdateDirectory);
        var script = Path.Combine(UpdateDirectory, "apply-update.cmd");
        var content =
            "@echo off\r\n" +
            "rem MicaAgenda 更新助手：等安装程序跑完，再把程序拉起来。\r\n" +
            // 给本进程一点退出时间，避免安装程序 taskkill 与正常退出打架
            "timeout /t 2 /nobreak >nul\r\n" +
            $"\"{installerPath}\" /SILENT /CLOSEAPPLICATIONS /NORESTART\r\n" +
            $"start \"\" \"{appExecutablePath}\"\r\n";
        File.WriteAllText(script, content);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    /// <summary>清掉上一次留下的安装包（检查到新版本时调用，避免临时目录越攒越多）。</summary>
    public static void CleanUpPreviousDownloads()
    {
        try
        {
            if (Directory.Exists(UpdateDirectory))
            {
                foreach (var file in Directory.GetFiles(UpdateDirectory))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "UpdateService.CleanUpPreviousDownloads");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 尽力而为：删不掉就留着，下次再试
        }
    }

    private static OSPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return OSPlatform.OSX;
        }

        return OSPlatform.Linux;
    }

    public void Dispose() => _http.Dispose();
}
