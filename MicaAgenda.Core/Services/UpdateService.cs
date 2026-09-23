using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.App.Services;

/// <summary>一个可下载的发布产物。</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long SizeBytes);

/// <summary>下载任务的一个分段（左闭右开的字节区间）。</summary>
public readonly record struct DownloadSegment(long Start, long Length)
{
    public long EndExclusive => Start + Length;
}

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
/// 版本比较、产物挑选、分段规划都是纯函数，便于单测。
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepoOwner = "bubu-LZY";
    public const string RepoName = "desktop_todo_Calendar";

    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>查元数据的超时（只对**检查更新**这一小段生效）。</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 下载"停滞"判定的阈值：连续这么久一个字节都没收到才算卡死。
    ///
    /// <para>用它替代一刀切的整体超时。安装包 50MB 上下，跨国链路上跑几十秒到几分钟都正常；
    /// 用一个固定总时长要么把正常下载掐断、要么就得放大到形同虚设。
    /// 而"多久没动静"才是真正区分「慢」和「死」的指标。</para>
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>小于这个体积不做分段 —— 并发带来的握手开销比省下的时间还多。</summary>
    public const long ParallelThresholdBytes = 8L * 1024 * 1024;

    /// <summary>每个分段的目标大小。</summary>
    private const long SegmentTargetBytes = 12L * 1024 * 1024;

    /// <summary>最多分几段。再多只是把带宽摊薄，收益递减、失败面反而变大。</summary>
    public const int MaxSegments = 6;

    private readonly HttpClient _http;
    private readonly Func<Version> _currentVersionProvider;

    public UpdateService(Func<Version>? currentVersionProvider = null)
    {
        var handler = new SocketsHttpHandler
        {
            // 连接建立（DNS + TCP + TLS 握手）的独立上限。
            //
            // HttpClient.Timeout 下面设成了"无限"（为了不掐断大文件下载），但"连都连不上"
            // 不能被无限拖住 —— api.github.com 在国内被 DNS 污染时，解析要么返回坏 IP、
            // 要么挂起，而 CancellationToken 对 DNS 解析阶段的打断在 Windows 上不可靠，
            // 光靠 CheckAsync 里的 CancelAfter(20s) 兜不住，用户看到的就是"一直检查更新"。
            // ConnectTimeout 只覆盖"建立连接"这一小段，不影响"连上之后慢慢收数据" ——
            // 那一段仍由停滞看门狗（30 秒没字节）保护，大文件下载不受影响。
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        _http = new HttpClient(handler)
        {
            // ⚠️ 这里**不能**设具体值。
            //
            // HttpClient.Timeout 是"整个请求（含把响应体读完）"的上限，不是"多久没响应"。
            // 老代码写的是 20 秒 —— 一个 50MB 的安装包意味着平均速度得跑到 2.5MB/s 才能活下来，
            // 慢一点的链路会在下到一半时被掐断。用户反馈"下载慢 / 下载不稳"，这一条是主因。
            // 现在改成无限，由"停滞看门狗"（30 秒没字节才算死）和调用方的取消令牌共同控制：
            // 慢但一直在动 → 继续下；彻底不动 → 中止并报错。
            Timeout = Timeout.InfiniteTimeSpan
        };

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

    /// <summary>
    /// 查询 GitHub 上的最新正式版，并和当前版本比一比。
    ///
    /// <paramref name="mirrorPrefix"/> 非空时，检查也走加速前缀（<c>前缀 + api.github.com/…</c>）。
    /// 之前只有"下载"走前缀、检查直连 —— 而 api.github.com 在国内被 DNS 污染 / 连接重置，
    /// 检查这步就先卡死了，根本走不到下载，用户填的加速前缀等于白填、表现为"一直检查更新"。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(
        string? mirrorPrefix = null,
        CancellationToken cancellationToken = default)
    {
        // HttpClient 的整体超时现在是"无限"（为了不掐断大文件下载），所以查询这一小段
        // 必须自己带上限：拉一下 release 元数据，20 秒足够，卡住就是网络有问题。
        // （DNS 解析阶段这个 token 可能打断不了 —— 那一段由构造函数的 ConnectTimeout 兜住。）
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        var token = timeout.Token;

        try
        {
            var apiUrl = BuildDownloadUrl(LatestReleaseApi, mirrorPrefix);
            using var response = await _http.GetAsync(apiUrl, token);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    false, false, null, null, null,
                    $"检查更新失败：GitHub 返回 {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(token);
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
            // 区分"用户取消"和"自己超时"：前者安静收场，后者要如实说明，
            // 否则用户点一次「检查更新」什么都不发生，只能反复点。
            return cancellationToken.IsCancellationRequested
                ? new UpdateCheckResult(false, false, null, null, null, "检查更新已取消")
                : new UpdateCheckResult(false, false, null, null, null,
                    $"检查更新超时（超过 {CheckTimeout.TotalSeconds:0} 秒没有响应）");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "UpdateService.Check");
            return new UpdateCheckResult(false, false, null, null, null, $"检查更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把一次总长度切成若干个下载分段（纯函数，便于单测）。
    ///
    /// <para><b>为什么值得分段</b>：GitHub 的发布文件在国外 CDN 上。跨国链路的特点是
    /// <b>单条 TCP 连接很难把带宽跑满</b>（丢包 + 往返延迟把窗口压住了），
    /// 而"多开几条连接拉同一个文件"能绕开这个限制 —— aria2 / 各类下载器对 GitHub
    /// 提速用的都是这一招。代价只是几次额外的请求，所以小文件不分段。</para>
    ///
    /// <para>分段长度尽量均匀，最后一段吃掉余数，保证拼起来正好是原文件。</para>
    /// </summary>
    public static IReadOnlyList<DownloadSegment> PlanSegments(long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return Array.Empty<DownloadSegment>();
        }

        var count = (int)Math.Clamp(
            (totalBytes + SegmentTargetBytes - 1) / SegmentTargetBytes, 1, MaxSegments);

        if (count <= 1)
        {
            return new[] { new DownloadSegment(0, totalBytes) };
        }

        var per = totalBytes / count;
        var segments = new List<DownloadSegment>(count);
        for (var i = 0; i < count; i++)
        {
            var start = per * i;
            var length = i == count - 1 ? totalBytes - start : per;
            segments.Add(new DownloadSegment(start, length));
        }

        return segments;
    }

    /// <summary>
    /// 下载安装包到临时目录，返回本地路径。已存在且大小一致时直接复用（重试 / 二次点击不用重下）。
    ///
    /// <para><paramref name="mirrorPrefix"/> 非空时，实际请求地址变成
    /// <c>prefix + 原始地址</c>，用于走自建 / 第三方加速（GitHub 在国外，这是最直接的解法）。
    /// 镜像只代理文件下载，不走 API 检查。</para>
    /// <paramref name="progress"/> 收到 0~1 的进度（宿主现在不再显示进度条，保留给日志与测试用）。
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        string? mirrorPrefix = null)
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
            var url = BuildDownloadUrl(asset.DownloadUrl, mirrorPrefix);
            await DownloadToFileAsync(url, asset.SizeBytes, partial, progress, cancellationToken);

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
    /// 把加速前缀拼到下载地址前面。前缀没写协议头就当作无效、忽略之
    /// （宁可不加速，也不要拼出一个必然失败的 URL）。
    /// </summary>
    public static string BuildDownloadUrl(string originalUrl, string? mirrorPrefix)
    {
        var prefix = (mirrorPrefix ?? string.Empty).Trim();
        if (prefix.Length == 0)
        {
            return originalUrl;
        }

        if (!prefix.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !prefix.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return originalUrl;
        }

        return prefix.EndsWith('/') ? prefix + originalUrl : prefix + "/" + originalUrl;
    }

    /// <summary>
    /// 真正落到文件上：能分段就并发分段，不能就退回单流。
    ///
    /// <para>两条路都挂同一个「停滞看门狗」：整整 <see cref="StallTimeout"/> 没有新字节才判定失败。
    /// 用它代替整体超时，是因为"慢"和"死"必须分开判 —— 前者该等，后者该报错。</para>
    /// </summary>
    private async Task DownloadToFileAsync(
        string url,
        long expectedBytes,
        string partial,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var total = await TryProbeTotalAsync(url, expectedBytes, cancellationToken);
        var segments = PlanSegments(total);

        // 分段要有意义才分：① 长度够大；② 服务器确实支持 Range（探测拿到 206 才算）；
        // ③ 不止一段。任一不满足就单流，免得为了提速反而更不稳。
        if (segments.Count > 1 && total >= ParallelThresholdBytes)
        {
            await DownloadSegmentedAsync(url, total, segments, partial, progress, cancellationToken);
        }
        else
        {
            await DownloadSingleStreamAsync(url, partial, progress, cancellationToken);
        }

        // 收尾校验：拿到的大小必须和预期一致，否则宁可报错也不要交出一个坏安装包。
        //
        // 注意：分段那条路会**预分配**整份大小，所以这里的大小检查对它恒真 ——
        // 分段下载的完整性其实由"每段必须读满自己那段、读不满就抛"来保证。
        // 这条检查真正兜住的是单流路径，以及"服务器没给 Content-Length"的情况。
        var actual = new FileInfo(partial).Length;
        if (expectedBytes > 0 && actual != expectedBytes)
        {
            throw new IOException($"下载不完整：预期 {expectedBytes} 字节，实际 {actual} 字节");
        }
    }

    /// <summary>
    /// 探测真实总长度，并顺带确认服务器是否支持 Range。
    /// 返回 0 表示"探不出来"（那就按单流走）。
    /// </summary>
    private async Task<long> TryProbeTotalAsync(string url, long expectedBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 只要第 0 个字节：既拿到 Content-Range 里的总长度，也验证 206 语义
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                // 服务器（或中间的代理 / 镜像）忽略了 Range —— 老老实实单流
                return 0;
            }

            var length = response.Content.Headers.ContentRange?.Length;
            return length is > 0 ? length.Value : 0;
        }
        catch (Exception ex)
        {
            // 探测失败不是错误：单流照样能下完
            AppLog.Error(ex, "UpdateService.Probe");
            return 0;
        }
    }

    /// <summary>单流下载（原来的路径，保留为兜底）。</summary>
    private async Task DownloadSingleStreamAsync(
        string url,
        string partial,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        using var watchdog = new StallWatchdog(StallTimeout);

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
                watchdog.Kick();
                if (total > 0)
                {
                    progress?.Report(Math.Clamp((double)received / total, 0, 1));
                }
            }
        }

        watchdog.ThrowIfStalled();
    }

    /// <summary>
    /// 分段并发下载。各分段直接写进同一个文件的对应偏移，不需要下完再拼接。
    /// </summary>
    private async Task DownloadSegmentedAsync(
        string url,
        long total,
        IReadOnlyList<DownloadSegment> segments,
        string partial,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var watchdog = new StallWatchdog(StallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdog.Token);

        // 预分配整份大小：各分段各自 seek 到自己的偏移写，互不重叠。
        using var handle = File.OpenHandle(
            partial,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            preallocationSize: total);

        var received = new long[1];

        await Task.WhenAll(segments.Select(segment => DownloadSegmentAsync(
            url, segment, handle, total, received, watchdog, progress, linked.Token)));

        watchdog.ThrowIfStalled();
    }

    private async Task DownloadSegmentAsync(
        string url,
        DownloadSegment segment,
        SafeFileHandle handle,
        long total,
        long[] received,
        StallWatchdog watchdog,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(segment.Start, segment.EndExclusive - 1);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // 必须拿到 206。若这里回 200，说明服务器把 Range 当成了普通请求、准备把**整个文件**
        // 返给每一个分段 —— 那样拼出来的文件是坏的，必须当场失败并退回单流。
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new IOException($"服务器没有按分段返回（HTTP {(int)response.StatusCode}）");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[81920];
        var offset = segment.Start;
        var remaining = segment.Length;

        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken);
            if (read <= 0)
            {
                throw new IOException("下载中途断开");
            }

            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, cancellationToken);
            offset += read;
            remaining -= read;
            watchdog.Kick();

            var done = Interlocked.Add(ref received[0], read);
            if (total > 0)
            {
                progress?.Report(Math.Clamp((double)done / total, 0, 1));
            }
        }
    }

    /// <summary>
    /// 「多久没收到字节」的看门狗。
    ///
    /// <para>存在的理由：<see cref="HttpClient"/> 的整体超时对 50MB 的安装包不是一个可用的指标
    /// （要么掐断正常下载，要么放大到形同虚设）。真正要区分的是「慢」和「死」 ——
    /// 只要字节还在动就继续等，连续 <c>idle</c> 时间一个字节都没有才判失败。</para>
    /// </summary>
    private sealed class StallWatchdog : IDisposable
    {
        private readonly TimeSpan _idle;
        private readonly CancellationTokenSource _cts = new();
        private readonly Timer _timer;
        private long _lastActivity;

        public StallWatchdog(TimeSpan idle)
        {
            _idle = idle;
            _lastActivity = Environment.TickCount64;
            // 每 2 秒检查一次即可：判定精度对 30 秒的阈值来说足够
            _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        /// <summary>令牌在被取消时触发 —— 把它和调用方的令牌一起 link 给请求。</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>每收到一段数据就踢一下。</summary>
        public void Kick() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

        /// <summary>下载结束后调用：确实是卡死的话，把"卡死"和"用户取消"区分开报出来。</summary>
        public void ThrowIfStalled()
        {
            if (_cts.IsCancellationRequested)
            {
                throw new TimeoutException($"连续 {_idle.TotalSeconds:0} 秒没有收到数据，下载已中止");
            }
        }

        private void Check()
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastActivity) < _idle.TotalMilliseconds)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch
            {
                // 取消已经是终态
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// 静默安装刚下载的安装包，并让程序在装完后自动回来。
    ///
    /// <para><b>为什么是 <c>/VERYSILENT</c> 而不是 <c>/SILENT</c></b>：Inno Setup 里
    /// <c>/SILENT</c> 只是"不提问"，<b>仍然会弹一个安装进度窗</b>；<c>/VERYSILENT</c> 才是什么都不显示。
    /// 用户明确要求"安装的时候也别显示进度"，所以必须是前者。配套的 <c>/SUPPRESSMSGBOXES</c>
    /// 用来压掉安装器自己的提示框（只在静默模式下有效）。</para>
    ///
    /// <para>不能直接 StartProcess(安装包) 了事：setup.iss 在安装前会 taskkill 掉本进程，
    /// 进程一死就没人负责「装完再启动」。所以交给一个独立的 cmd —— 它先等安装程序结束，
    /// 再 start 本程序。cmd 不在安装程序的清理名单里，能一直活到安装结束。</para>
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
            $"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART\r\n" +
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
