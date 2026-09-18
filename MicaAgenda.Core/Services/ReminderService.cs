using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 定时提醒：在全局设定时间点，检查当日任务，通过飞书/企业微信 webhook 推送。
/// 支持开机补发：程序启动时若已超过提醒时间且当天尚未提醒，则立即补发一次。
/// </summary>
public sealed class ReminderService : IDisposable
{
    private readonly CalendarData _data;
    private readonly object _syncRoot;
    private readonly Func<AppConfig> _configProvider;
    private readonly System.Threading.Timer _timer;
    private readonly HttpClient _httpClient;
    private readonly object _stateLock = new();
    private readonly Action? _onDataChanged;
    private DateOnly _lastReminderDate;
    private int _checking;

    /// <param name="onDataChanged">
    /// 逐条任务的到点提醒推完后要落一个「已推过」标记，需要宿主把数据标记为脏并保存。
    /// 由宿主注入（Avalonia / WPF 宿主都传自己的刷新+落盘回调）；不传则只在内存里标记。
    /// </param>
    public ReminderService(
        CalendarData data,
        object syncRoot,
        Func<AppConfig> configProvider,
        Action? onDataChanged = null)
    {
        _data = data;
        _syncRoot = syncRoot;
        _configProvider = configProvider;
        _onDataChanged = onDataChanged;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _lastReminderDate = LoadLastReminderDate();
        // 立即执行一次（处理开机补发），随后每 30 秒检查一次
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private async Task CheckAsync()
    {
        // 定时器每 30 秒触发一次，若上一次推送尚未完成（网络慢/多渠道路由），
        // 这里可能重入导致重复提醒，用原子标记挡掉并发执行。
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            var config = _configProvider();
            if (!config.ReminderEnabled)
            {
                return;
            }

            var hasFeishu = !string.IsNullOrWhiteSpace(config.FeishuWebhook);
            var hasWeCom = !string.IsNullOrWhiteSpace(config.WeComWebhook);
            if (!hasFeishu && !hasWeCom)
            {
                // 没有推送渠道时两种提醒都发不出去。这里刻意不写任何「已提醒」标记，
                // 否则用户当天稍后补上 webhook，就再也收不到今天的提醒了。
                return;
            }

            // 逐条任务的到点提醒 + 每日汇总提醒。分开各自兜底：
            // 逐条提醒里某一条推送失败，不应该拖住每日汇总。
            await CheckTaskRemindersAsync(config.FeishuWebhook, config.WeComWebhook);
            await CheckDailyReminderAsync(config, hasFeishu, hasWeCom);
        }
        catch (Exception ex)
        {
            // 定时器回调是 async Task（线程池），异常不会终止进程，但仍有必要记日志兜底
            AppLog.Error(ex, "ReminderService");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// 逐条任务的到点提醒：任务设了具体时间 → 在「任务时间 - 提前量」推送
    /// 「【桌面日历任务提醒】xxx 将在 14:00 开始（还有 30 分钟）」。
    ///
    /// 一条任务可勾选多个提醒档位（如「提前一天」+「提前30分钟」+「到时提醒」），
    /// 每个档位各推一次、各自去重（见 <see cref="CalendarTask.DueReminderLeads"/>）；
    /// 已完成的任务不再推；已经过期到隔天的档位只记标记、不补发，避免一次开机蹦出十几条旧提醒。
    /// </summary>
    private async Task CheckTaskRemindersAsync(string feishuWebhook, string wecomWebhook)
    {
        var hasFeishu = !string.IsNullOrWhiteSpace(feishuWebhook);
        var hasWeCom = !string.IsNullOrWhiteSpace(wecomWebhook);
        var now = DateTime.Now;

        // 快照里同时记下每个档位的触发时刻：webhook 是锁外发送（最坏要十几秒），
        // 发送期间用户可能已经改了任务时刻/提醒。回包落「已推」标记前必须再校验一次，
        // 否则旧一轮推送的标记会盖住 ResetReminder，让新计划的提醒永远不响。
        List<(CalendarTask Task, List<(int Lead, DateTime TriggerAt)> Leads)> due;
        List<(CalendarTask Task, List<(int Lead, DateTime TriggerAt)> Leads)> expired;
        lock (_syncRoot)
        {
            due = _data.Tasks
                .Select(task => (
                    task,
                    leads: task.DueReminderLeads(now)
                        .Select(lead => (lead, trigger: task.ReminderTriggerAt(lead)))
                        .ToList()))
                .Where(item => item.leads.Count > 0)
                .ToList();
            expired = _data.Tasks
                .Select(task => (
                    task,
                    leads: task.ExpiredReminderLeads(now)
                        .Select(lead => (lead, trigger: task.ReminderTriggerAt(lead)))
                        .ToList()))
                .Where(item => item.leads.Count > 0)
                .ToList();
        }

        if (expired.Count > 0)
        {
            var markedAny = false;
            lock (_syncRoot)
            {
                var stamp = DateTimeOffset.Now;
                foreach (var (task, leads) in expired)
                {
                    foreach (var (lead, triggerAt) in leads)
                    {
                        // 发送窗口内计划被改过：触发时刻已经对不上，这个过期标记属于旧计划，丢弃
                        if (task.IsCompleted
                            || !task.AllReminderLeads.Contains(lead)
                            || task.ReminderTriggerAt(lead) != triggerAt
                            || task.IsReminderFired(lead))
                        {
                            continue;
                        }

                        task.MarkReminderSent(lead, stamp);
                        markedAny = true;
                    }
                }
            }

            if (markedAny)
            {
                _onDataChanged?.Invoke();
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        var sentAny = false;
        foreach (var (task, leads) in due.OrderBy(item => item.Task.EarliestReminderTriggerAt()))
        {
            // 同一任务多档位补发时，按触发时刻先后发（提前量大的先响），
            // 不能先发「时间到了」再补一条「还有 30 分钟」。
            foreach (var (lead, triggerAt) in leads.OrderByDescending(item => item.Lead))
            {
                sentAny |= await SendOneTaskReminderAsync(
                    task, lead, triggerAt, hasFeishu, feishuWebhook, hasWeCom, wecomWebhook);
            }
        }

        if (sentAny)
        {
            _onDataChanged?.Invoke();
        }
    }

    /// <summary>
    /// 推送单条任务的单个档位提醒；两个渠道都失败时不打标记，下一轮（30 秒后）重试。
    /// <paramref name="scheduledTriggerAt"/> 是本轮快照时该档位的触发时刻：
    /// webhook 往返可能耗时十几秒，期间用户若改了任务时刻/提醒，回包后校验失败就不落标记，
    /// 避免旧一轮推送把新计划的「已推」状态污染掉。
    /// 返回是否至少有一个渠道送达（且标记成功落账）。
    /// </summary>
    private async Task<bool> SendOneTaskReminderAsync(
        CalendarTask task,
        int lead,
        DateTime scheduledTriggerAt,
        bool hasFeishu,
        string feishuWebhook,
        bool hasWeCom,
        string wecomWebhook)
    {
        var text = BuildTaskReminderText(task, lead);
        var delivered = false;

        if (hasFeishu)
        {
            try
            {
                await SendFeishuAsync(feishuWebhook, text);
                delivered = true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "ReminderService.TaskFeishu");
            }
        }

        if (hasWeCom)
        {
            try
            {
                await SendWeComAsync(wecomWebhook, text);
                delivered = true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "ReminderService.TaskWeCom");
            }
        }

        // 所有渠道都失败时不标记，30 秒后的下一轮会重试。
        if (!delivered)
        {
            return false;
        }

        lock (_syncRoot)
        {
            // 发送期间任务被完成 / 档位被删改 / 时刻被移动：这条推送属于旧计划，不落账。
            // 新计划该响还会响（编辑入口已经 ResetReminder），旧标记也不会挡住它。
            if (task.IsCompleted
                || !task.AllReminderLeads.Contains(lead)
                || task.ReminderTriggerAt(lead) != scheduledTriggerAt
                || task.IsReminderFired(lead))
            {
                return false;
            }

            task.MarkReminderSent(lead, DateTimeOffset.Now);
        }

        return true;
    }

    /// <summary>
    /// 每日汇总提醒：到点后把「当天全部任务」推一条清单。
    /// 与逐条提醒共用 <see cref="AppConfig.ReminderEnabled"/> 总开关与同一对 webhook。
    /// </summary>
    private async Task CheckDailyReminderAsync(AppConfig config, bool hasFeishu, bool hasWeCom)
    {
        if (!TimeOnly.TryParse(config.ReminderTime, out var reminderTime))
        {
            return;
        }

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var currentTime = TimeOnly.FromDateTime(now);

        // 今天已提醒过，跳过
        if (GetLastReminderDate() == today)
        {
            return;
        }

        // 还没到提醒时间，等待
        if (currentTime < reminderTime)
        {
            return;
        }

        // 已到（或已过）提醒时间：发送提醒
        List<CalendarTask> tasks;
        lock (_syncRoot)
        {
            tasks = _data.Tasks.Where(t => t.Date == today).ToList();
        }

        if (tasks.Count == 0)
        {
            // 当天无任务，记录已提醒，避免反复检查
            SetLastReminderDate(today);
            return;
        }

        var text = BuildReminderText(today, tasks);

        var anySent = false;
        if (hasFeishu)
        {
            try
            {
                await SendFeishuAsync(config.FeishuWebhook, text);
                anySent = true;
            }
            catch (Exception ex)
            {
                // 渠道失败要记日志：webhook 填错 / 群被移除时用户应该有线索，
                // 否则配置页一切看似正常，到点却什么都不响，会以为是程序坏了
                AppLog.Error(ex, "ReminderService.Feishu");
            }
        }

        if (hasWeCom)
        {
            try
            {
                await SendWeComAsync(config.WeComWebhook, text);
                anySent = true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "ReminderService.WeCom");
            }
        }

        // 所有渠道都失败时不标记「已提醒」，后续 30 秒检查会继续重试。
        if (!anySent)
        {
            return;
        }

        SetLastReminderDate(today);
    }

    private DateOnly GetLastReminderDate()
    {
        lock (_stateLock)
        {
            return _lastReminderDate;
        }
    }

    private void SetLastReminderDate(DateOnly date)
    {
        lock (_stateLock)
        {
            _lastReminderDate = date;
        }

        SaveLastReminderDate(date);
    }

    private static string GetStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "MicaAgenda", "reminder-state.json");
    }

    private static DateOnly LoadLastReminderDate()
    {
        try
        {
            var path = GetStatePath();
            if (!System.IO.File.Exists(path))
            {
                return DateOnly.MinValue;
            }

            var json = System.IO.File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("lastReminderDate", out var el) &&
                DateOnly.TryParse(el.GetString(), out var date))
            {
                return date;
            }
        }
        catch
        {
            // 忽略读取失败
        }

        return DateOnly.MinValue;
    }

    private static void SaveLastReminderDate(DateOnly date)
    {
        try
        {
            var path = GetStatePath();
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(new { lastReminderDate = date.ToString("yyyy-MM-dd") });
            System.IO.File.WriteAllText(path, json);
        }
        catch
        {
            // 忽略写入失败
        }
    }

    /// <summary>
    /// 单条任务某个档位的到点提醒文案。
    /// 「提前一天」专门点明是次日的任务，免得收到时以为提醒错了日子。
    /// </summary>
    private static string BuildTaskReminderText(CalendarTask task, int lead)
    {
        // 任务时刻 = 老数据里存过的时间，否则是当天默认的 9:00。
        var timeText = (task.Time ?? CalendarTask.DefaultTime).ToString("HH:mm");
        lead = lead < 0 ? 0 : lead;

        // 提前量按整天算时（如「提前一天」），提醒是在任务日之前推送的，
        // 光写一个 14:00 会让人以为是今天的事，带上任务日期（今天 / 明天 / M月d日）。
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dayText = task.Date == today
            ? string.Empty
            : task.Date == today.AddDays(1)
                ? "明天 "
                : $"{task.Date.Month}月{task.Date.Day}日 ";

        var sb = new StringBuilder();
        sb.AppendLine("【桌面日历任务提醒】");
        if (lead > 0)
        {
            sb.AppendLine($"「{task.Title}」将在 {dayText}{timeText} 开始（还有 {Helpers.TimeText.FormatLead(lead)}）");
        }
        else
        {
            sb.AppendLine($"「{task.Title}」的时间到了（{dayText}{timeText}）");
        }

        if (task.IsImportant)
        {
            sb.AppendLine("⭐ 重要任务");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildReminderText(DateOnly date, List<CalendarTask> tasks)
    {
        var pending = tasks.Where(t => !t.IsCompleted).ToList();
        var done = tasks.Where(t => t.IsCompleted).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"【桌面日历提醒】{date:yyyy年M月d日}");
        sb.AppendLine($"今日任务共 {tasks.Count} 项，待完成 {pending.Count} 项：");

        var index = 1;
        foreach (var task in tasks.OrderBy(t => t.IsCompleted).ThenByDescending(t => t.IsImportant))
        {
            var mark = task.IsCompleted ? "✅" : "⬜";
            var star = task.IsImportant ? "⭐" : "";
            sb.AppendLine($"{index}. {mark} {task.Title}{star}");
            index++;
        }

        if (done.Count > 0)
        {
            sb.AppendLine($"已完成 {done.Count} 项，继续加油！");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task SendFeishuAsync(string webhook, string text)
    {
        var payload = new
        {
            msg_type = "text",
            content = new { text }
        };
        await PostAsync(webhook, payload);
    }

    private async Task SendWeComAsync(string webhook, string text)
    {
        var payload = new
        {
            msgtype = "text",
            text = new { content = text }
        };
        await PostAsync(webhook, payload);
    }

    private async Task PostAsync(string webhook, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(webhook, content);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _httpClient.Dispose();
    }
}
