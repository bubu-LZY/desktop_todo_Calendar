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
/// 触发：
/// - 启用同步后，启动 20 秒做第一次同步，之后每小时一次；
/// - 用户在日历里勾选/取消完成复习任务时，立即把最新状态推给 mindmap（见 MainWindow 的
///   PushCompletionToMindMapAsync），不受本定时器影响。
/// </summary>
public sealed class MindMapReviewSyncService : IDisposable
{
    private const string ReviewPrefix = "[MM复习]";
    private const string LegacyPrefix = "[复习]";
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(20);

    private readonly CalendarData _data;
    private readonly object _syncRoot;
    private readonly Func<AppConfig> _configProvider;
    private readonly Action _onDataChanged;
    private readonly HttpClient _http;
    private readonly System.Threading.Timer _timer;
    private int _running;

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
        _timer = new System.Threading.Timer(_ => _ = SyncAsync(), null, FirstRunDelay, SyncInterval);
    }

    /// <summary>最近一次同步结果（供设置窗口展示）。</summary>
    public string LastResult { get; private set; } = string.Empty;
    public DateTimeOffset? LastSyncAt { get; private set; }

    public void Dispose() => _timer.Dispose();

    private sealed record ReviewEntry(string Date, string Title, bool Completed, long StatusUpdatedAt);

    private static bool HasReviewPrefix(string title) =>
        title.StartsWith(ReviewPrefix, StringComparison.OrdinalIgnoreCase)
        || title.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTitle(string title)
    {
        var t = title?.Trim() ?? string.Empty;
        if (t.StartsWith(ReviewPrefix, StringComparison.OrdinalIgnoreCase))
            t = t[ReviewPrefix.Length..].Trim();
        else if (t.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
            t = t[LegacyPrefix.Length..].Trim();
        return t;
    }

    private static DateTimeOffset FromUnixMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();

    private static long ToUnixMs(DateTimeOffset dto) => dto.ToUnixTimeMilliseconds();

    private string BaseUrl =>
        string.IsNullOrWhiteSpace(_configProvider().MindMapBaseUrl)
            ? "http://127.0.0.1:17800"
            : _configProvider().MindMapBaseUrl.Trim().TrimEnd('/');

    /// <summary>立即执行一次同步（设置窗口「立即同步」按钮）。</summary>
    public Task<string> SyncNowAsync() => SyncAsync();

    private async Task<string> SyncAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return "同步正在进行中";
        }

        try
        {
            var config = _configProvider();
            if (!config.SyncMyMindMapEnabled || string.IsNullOrWhiteSpace(config.MyMindMapToken))
            {
                return "未开启同步或未填写 Token";
            }

            var entries = await FetchReviewPlanAsync(config.MyMindMapToken);
            if (entries is null)
            {
                return "无法连接 my-mindmap agent（请确认程序已启动且 Token 正确）";
            }

            var toPush = new List<CalendarTask>();
            int pulled = 0;

            lock (_syncRoot)
            {
                var reviewTasks = _data.Tasks.Where(t => HasReviewPrefix(t.Title)).ToList();

                foreach (var e in entries)
                {
                    var normalized = NormalizeTitle(e.Title);
                    if (string.IsNullOrWhiteSpace(normalized)) normalized = "复习任务";
                    if (!DateOnly.TryParse(e.Date, out var date)) continue;

                    var match = reviewTasks.FirstOrDefault(t =>
                        t.Date == date && NormalizeTitle(t.Title) == normalized);

                    var remoteCompleted = e.Completed;
                    var remoteTs = e.StatusUpdatedAt;

                    if (match is null)
                    {
                        // 本地还没有这条复习任务：新增，并带对端时间戳写入状态
                        var task = new CalendarTask
                        {
                            Date = date,
                            Title = ReviewPrefix + normalized,
                            IsImportant = false
                        };
                        task.ApplySyncedStatus(remoteCompleted, FromUnixMs(remoteTs > 0 ? remoteTs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                        _data.Tasks.Add(task);
                        reviewTasks.Add(task);
                        pulled++;
                        continue;
                    }

                    var localCompleted = match.IsCompleted;
                    if (localCompleted == remoteCompleted) continue;

                    var localTs = ToUnixMs(match.StatusTimestamp);
                    if (remoteTs > localTs)
                    {
                        // 对端更新：拉回
                        match.ApplySyncedStatus(remoteCompleted, FromUnixMs(remoteTs));
                        pulled++;
                    }
                    else
                    {
                        // 本端更新：推给对端
                        toPush.Add(match);
                    }
                }
            }

            int pushed = 0;
            foreach (var task in toPush)
            {
                if (await PushStatusAsync(config.MyMindMapToken, task)) pushed++;
            }

            if (pulled > 0 || pushed > 0)
            {
                _onDataChanged();
            }

            LastSyncAt = DateTimeOffset.Now;
            LastResult = $"同步完成：拉取 {pulled}、推送 {pushed}";
            return LastResult;
        }
        catch (Exception ex)
        {
            LastResult = $"同步失败：{ex.Message}";
            return LastResult;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<List<ReviewEntry>?> FetchReviewPlanAsync(string token)
    {
        try
        {
            var url = $"{BaseUrl}/api/desk-calendar/review-plan?token={Uri.EscapeDataString(token)}";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
            if (!root.TryGetProperty("tasks", out var tasksEl) || tasksEl.ValueKind != JsonValueKind.Array) return null;

            var result = new List<ReviewEntry>();
            foreach (var t in tasksEl.EnumerateArray())
            {
                var date = t.TryGetProperty("date", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                var title = t.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty;
                var completed = t.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True;
                var ts = t.TryGetProperty("statusUpdatedAt", out var s) && s.TryGetInt64(out var v) ? v : 0L;
                result.Add(new ReviewEntry(date, title, completed, ts));
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把日历端某条复习任务的最新状态推给 my-mindmap agent。</summary>
    public async Task<bool> PushStatusAsync(string token, CalendarTask task)
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
            using var resp = await _http.PostAsync($"{BaseUrl}/api/desk-calendar/status", content);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
