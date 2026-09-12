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
    private DateOnly _lastReminderDate;
    private int _checking;

    public ReminderService(CalendarData data, object syncRoot, Func<AppConfig> configProvider)
    {
        _data = data;
        _syncRoot = syncRoot;
        _configProvider = configProvider;
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

            var hasFeishu = !string.IsNullOrWhiteSpace(config.FeishuWebhook);
            var hasWeCom = !string.IsNullOrWhiteSpace(config.WeComWebhook);

            if (!hasFeishu && !hasWeCom)
            {
                // 没有配置任何推送渠道时不记录“已提醒”，否则用户当天稍后补上 webhook
                // 也不会再收到今天的提醒。
                return;
            }

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

            // 所有渠道都失败时不标记“已提醒”，后续 30 秒检查会继续重试。
            if (!anySent)
            {
                return;
            }

            SetLastReminderDate(today);
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
