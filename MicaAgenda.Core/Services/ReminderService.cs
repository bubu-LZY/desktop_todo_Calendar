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
        _timer = new System.Threading.Timer(_ => CheckAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private async void CheckAsync()
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
            // 定时器回调是 async void（线程池），异常会终止进程，必须在此兜底
            AppLog.Error(ex, "ReminderService");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// 逐条任务的到点提醒：任务设了具体时间 → 在「任务时间 - 提前量」推送一次
    /// 「【桌面日历任务提醒】xxx 将在 14:00 开始（还有 30 分钟）」。
    ///
    /// 每条任务只推一次（<see cref="CalendarTask.ReminderSentAt"/> 去重并持久化），
    /// 已完成的任务不再推；已经过期到隔天的提醒只记标记、不补发，避免一次开机蹦出十几条旧提醒。
    /// </summary>
    private async Task CheckTaskRemindersAsync(string feishuWebhook, string wecomWebhook)
    {
        var hasFeishu = !string.IsNullOrWhiteSpace(feishuWebhook);
        var hasWeCom = !string.IsNullOrWhiteSpace(wecomWebhook);
        var now = DateTime.Now;

        List<CalendarTask> due;
        List<CalendarTask> expired;
        lock (_syncRoot)
        {
            due = _data.Tasks.Where(task => task.ShouldFireReminder(now)).ToList();
            expired = _data.Tasks.Where(task => task.IsReminderExpired(now)).ToList();
        }

        if (expired.Count > 0)
        {
            lock (_syncRoot)
            {
                foreach (var task in expired)
                {
                    task.MarkReminderSent(DateTimeOffset.Now);
                }
            }

            _onDataChanged?.Invoke();
        }

        if (due.Count == 0)
        {
            return;
        }

        var sentAny = false;
        foreach (var task in due.OrderBy(task => task.ReminderTriggerAt()))
        {
            var text = BuildTaskReminderText(task);
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
                continue;
            }

            lock (_syncRoot)
            {
                task.MarkReminderSent(DateTimeOffset.Now);
            }

            sentAny = true;
        }

        if (sentAny)
        {
            _onDataChanged?.Invoke();
        }
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

    /// <summary>单条任务的到点提醒文案。</summary>
    private static string BuildTaskReminderText(CalendarTask task)
    {
        // 任务时刻 = 老数据里存过的时间，否则是当天默认的 9:00。
        var timeText = (task.Time ?? CalendarTask.DefaultTime).ToString("HH:mm");
        var lead = task.ReminderLeadMinutes ?? 0;

        var sb = new StringBuilder();
        sb.AppendLine("【桌面日历任务提醒】");
        if (lead > 0)
        {
            sb.AppendLine($"「{task.Title}」将在 {timeText} 开始（还有 {Helpers.TimeText.FormatLead(lead)}）");
        }
        else
        {
            sb.AppendLine($"「{task.Title}」的时间到了（{timeText}）");
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
