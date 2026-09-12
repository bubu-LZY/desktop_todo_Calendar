using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 与 my-mindmap agent 的复习计划双向同步。
///
/// 范围：仅处理标题带 [MM复习]（旧版 [复习]）前缀的任务 —— 这类任务由 my-mindmap agent
/// 的「复习计划」生成，用户/其他 AI 自己建的日历任务一律不参与。
///
/// 仲裁：以「状态最后变更时间」为准的最后写入胜出（LWW）。
/// 场景：mindmap 端 10:00 勾选完成、10:01 取消 —— 取消的时间戳更新，同步时以"未完成"为准，
/// 不会把误触的完成状态留在日历里。
///
/// 对端仍是复习任务「存在与否」的权威：计划里没有的复习任务（重复项、对端已删掉的复习周期）
/// 会在同步时被清掉，保证两边集合一致。
///
/// 触发：
/// - 启用同步后，启动 20 秒做第一次同步，之后每小时一次；同步失败（对端没开）则 5 分钟后就重试，
///   不必干等一小时；
/// - 用户在日历里勾选/取消完成复习任务时，立即把最新状态推给 mindmap（见 MainWindow 的
///   PushCompletionToMindMapAsync），不受本定时器影响。
///
/// 比对与仲裁的纯逻辑在 <see cref="ReviewSyncPlanner"/>，本类只负责取数、落盘与 HTTP。
/// </summary>
public sealed class MindMapReviewSyncService : IDisposable
{
    private const string DefaultBaseUrl = "http://127.0.0.1:17800";

    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(20);

    private readonly CalendarData _data;
    private readonly object _syncRoot;
    private readonly Func<AppConfig> _configProvider;
    private readonly Action _onDataChanged;
    private readonly HttpClient _http;
    private readonly System.Threading.Timer _timer;
    private int _running;
    private bool _disposed;

    public MindMapReviewSyncService(
        CalendarData data,
        object syncRoot,
        Func<AppConfig> configProvider,
        Action onDataChanged)
    {
        _data = data;
        _syncRoot = syncRoot;
        _configProvider = configProvider;
        _onDataChanged = onDataChanged;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _timer = new System.Threading.Timer(_ => _ = SyncAsync(null), null, FirstRunDelay, SyncInterval);
    }

    /// <summary>最近一次同步结果（供设置窗口展示）。</summary>
    public string LastResult { get; private set; } = string.Empty;
    public DateTimeOffset? LastSyncAt { get; private set; }

    /// <summary>最近一次同步是否真的和对方通上了。没通时下一次会很快重试、而不是等满一小时。</summary>
    public bool LastSyncSucceeded { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _timer.Dispose();
        }
        catch
        {
            // 忽略关闭异常
        }

        _http.Dispose();
    }

    /// <summary>按已保存的配置同步一次（定时任务与「立即同步」都走这里）。</summary>
    public Task<string> SyncNowAsync() => SyncAsync(null);

    /// <summary>
    /// 设置面板「立即同步」：用面板里当前填写的地址 / Token 同步一次。
    /// 必须带上面板里的值——用户通常是先点「立即同步」验证通了才点保存，
    /// 此时开关、地址、Token 都还没落盘，只读已保存配置会直接回一句"未开启同步"。
    /// 这是用户的显式动作，因此不受同步开关限制。
    /// </summary>
    public Task<string> SyncNowAsync(string? baseUrl, string? token) => SyncAsync(new SyncTarget(baseUrl, token));

    private sealed record SyncTarget(string? BaseUrl, string? Token);

    private async Task<string> SyncAsync(SyncTarget? target)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return "同步正在进行中";
        }

        try
        {
            var config = _configProvider();
            var token = FirstNonEmpty(target?.Token, config.MyMindMapToken);
            var baseUrl = ResolveBaseUrl(FirstNonEmpty(target?.BaseUrl, config.MindMapBaseUrl));

            // 定时同步才看开关；「立即同步」是用户的显式动作，勾没勾都照做。
            if (target is null && !config.SyncMyMindMapEnabled)
            {
                LastSyncSucceeded = true;
                return "未开启与 my-mindmap agent 的同步";
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                LastSyncSucceeded = false;
                LastResult = "未填写 Token：请把 my-mindmap agent 设置里的本地 HTTP Token 粘贴过来";
                return LastResult;
            }

            var (entries, error) = await FetchReviewPlanAsync(baseUrl, token);
            if (entries is null)
            {
                LastSyncSucceeded = false;
                LastResult = error ?? "无法连接 my-mindmap agent";
                return LastResult;
            }

            ReviewSyncPlan plan;
            lock (_syncRoot)
            {
                var reviewTasks = _data.Tasks
                    .Where(t => ReviewSyncPlanner.HasReviewPrefix(t.Title))
                    .ToList();
                plan = ReviewSyncPlanner.Build(entries, reviewTasks, DateTimeOffset.Now);
            }

            var created = 0;
            var pulled = 0;
            var deleted = 0;

            // 本端的数据改动统一在一把锁里完成，避免后台线程与 UI 线程交叉修改集合。
            lock (_syncRoot)
            {
                foreach (var item in plan.Creates)
                {
                    var task = new CalendarTask
                    {
                        Id = item.Id,
                        Date = item.Date,
                        Title = item.Title,
                        IsImportant = false,
                        CreatedAt = DateTimeOffset.Now
                    };
                    task.ApplySyncedStatus(item.Completed, item.At);
                    _data.Tasks.Add(task);
                    created++;
                }

                foreach (var item in plan.Pulls)
                {
                    var task = _data.Tasks.FirstOrDefault(t => t.Id == item.TaskId);
                    if (task is null)
                    {
                        continue;
                    }

                    task.ApplySyncedStatus(item.Completed, item.At);
                    pulled++;
                }

                foreach (var item in plan.Deletes)
                {
                    var task = _data.Tasks.FirstOrDefault(t => t.Id == item.TaskId);
                    if (task is null)
                    {
                        continue;
                    }

                    _data.Tasks.Remove(task);
                    deleted++;
                }
            }

            // 推送要走网络，放在锁外做，避免阻塞 UI 线程读写数据。
            var pushed = 0;
            foreach (var item in plan.Pushes)
            {
                CalendarTask? task;
                lock (_syncRoot)
                {
                    task = _data.Tasks.FirstOrDefault(t => t.Id == item.TaskId);
                }

                if (task is not null && await PushStatusAsync(baseUrl, token, task))
                {
                    pushed++;
                }
            }

            if (created > 0 || pulled > 0 || deleted > 0 || pushed > 0)
            {
                _onDataChanged();
            }

            LastSyncSucceeded = true;
            LastSyncAt = DateTimeOffset.Now;
            LastResult = $"同步完成：新增 {created}、推送 {pushed}、拉取 {pulled}、删除 {deleted}";
            return LastResult;
        }
        catch (Exception ex)
        {
            LastSyncSucceeded = false;
            LastResult = $"同步失败：{ex.Message}";
            return LastResult;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            Reschedule();
        }
    }

    /// <summary>
    /// 重排下一次定时同步。对端没启动是常态，失败后 5 分钟就再来一次；
    /// 正常则回到每小时一次（与首次启动后的节奏一致）。
    /// </summary>
    private void Reschedule()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _timer.Change(LastSyncSucceeded ? SyncInterval : RetryInterval, SyncInterval);
        }
        catch (ObjectDisposedException)
        {
            // 程序退出时可能正好撞上定时回调，忽略即可
        }
    }

    private async Task<(List<ReviewSyncEntry>? Entries, string? Error)> FetchReviewPlanAsync(string baseUrl, string token)
    {
        try
        {
            var url = $"{baseUrl}/api/desk-calendar/review-plan?token={Uri.EscapeDataString(token)}";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                return (null, DescribeHttpFailure(resp.StatusCode));
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var reason = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString()
                    : "返回内容异常";
                return (null, $"my-mindmap agent 拒绝同步：{reason}");
            }

            if (!root.TryGetProperty("tasks", out var tasksEl) || tasksEl.ValueKind != JsonValueKind.Array)
            {
                return (null, "my-mindmap agent 返回的复习计划格式不正确");
            }

            var entries = new List<ReviewSyncEntry>();
            foreach (var t in tasksEl.EnumerateArray())
            {
                var dateText = t.TryGetProperty("date", out var d) ? d.GetString() : null;
                if (!DateOnly.TryParse(dateText, out var date))
                {
                    // 日期缺失/格式不对的条目没法配对，丢弃比误配更安全
                    continue;
                }

                var title = t.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String
                    ? ti.GetString() ?? string.Empty
                    : string.Empty;
                var completed = t.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True;
                var ts = t.TryGetProperty("statusUpdatedAt", out var s) && s.TryGetInt64(out var v) ? v : 0L;
                entries.Add(new ReviewSyncEntry(date, title, completed, ts));
            }

            return (entries, null);
        }
        catch (Exception ex)
        {
            return (null, $"无法连接 my-mindmap agent（{baseUrl}）：{ex.Message}。请确认 my-mindmap agent 正在运行，且地址与 Token 填的是它的本地 HTTP 服务。");
        }
    }

    /// <summary>
    /// 把 HTTP 错误翻译成能照着修的提示：
    /// 401 是 Token 问题，503 一般是对端主界面没打开（复习计划要由渲染进程回答，纯后台给不出来）。
    /// </summary>
    private static string DescribeHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Token 无效或已过期：请在 my-mindmap agent 的设置里重新复制本地 HTTP Token（对端每 60 天会自动轮换一次）",
        HttpStatusCode.ServiceUnavailable => "my-mindmap agent 主界面未就绪：请打开它的主窗口后重试",
        _ => $"my-mindmap agent 返回 HTTP {(int)status}"
    };

    /// <summary>把日历端某条复习任务的最新状态推给 my-mindmap agent（用已保存的配置）。</summary>
    public Task<bool> PushStatusAsync(string token, CalendarTask task) =>
        PushStatusAsync(ResolveBaseUrl(_configProvider().MindMapBaseUrl), token, task);

    private async Task<bool> PushStatusAsync(string baseUrl, string token, CalendarTask task)
    {
        try
        {
            var payload = new
            {
                token,
                title = task.Title,
                date = task.Date.ToString("yyyy-MM-dd"),
                isCompleted = task.IsCompleted,
                updatedAt = task.StatusTimestamp
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{baseUrl}/api/desk-calendar/status", content);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            // 推送是尽力而为：失败不影响本机操作，下一次定时同步会再对齐
            return false;
        }
    }

    private static string ResolveBaseUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? DefaultBaseUrl : url.Trim().TrimEnd('/');

    private static string FirstNonEmpty(string? first, string? fallback) =>
        !string.IsNullOrWhiteSpace(first) ? first!.Trim() : (fallback ?? string.Empty).Trim();
}
