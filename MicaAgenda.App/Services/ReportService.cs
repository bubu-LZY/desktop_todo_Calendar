using System.Globalization;
using System.IO;
using System.Text.Json;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 定时报告：按周 / 按月生成「任务完成情况」报告并推送到飞书 / 企业微信 / 自定义 webhook。
///
/// 设计要点：
/// 1. 用「周期键」（如 2026-W36 / 2026-09）而不是日期做幂等标记 —— 同一周期内只发一次，
///    重启、跨天、时间到了之后又改配置都不会重复推送。
/// 2. 到点开机补发：程序启动后若当前周期还没发过，且已过发送时刻，会立即补发一次。
/// 3. 渠道失败会按周期重试，但带次数上限，避免 webhook 填错时每分钟重试刷屏。
/// </summary>
public sealed class ReportService : IDisposable
{
    /// <summary>同一周期内最多重试的次数；超过后放弃，等下一个周期。</summary>
    private const int MaxAttemptsPerPeriod = 5;

    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = false };

    private readonly CalendarData _data;
    private readonly object _syncRoot;
    private readonly Func<AppConfig> _configProvider;
    private readonly System.Threading.Timer _timer;
    private readonly WebhookSender _sender;
    private readonly object _stateLock = new();
    private int _running;
    private string _lastPeriodKey = string.Empty;
    private string _attemptPeriodKey = string.Empty;
    private int _failedAttempts;

    public ReportService(CalendarData data, object syncRoot, Func<AppConfig> configProvider)
    {
        _data = data;
        _syncRoot = syncRoot;
        _configProvider = configProvider;
        _sender = new WebhookSender(TimeSpan.FromSeconds(20));
        LoadState();
        // 立即执行一次（处理开机补发），随后每 60 秒检查一次
        _timer = new System.Threading.Timer(_ => CheckAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    /// <summary>报告已发出时触发（参数为报告纯文本，方便 UI 提示）。</summary>
    public event Action<string>? ReportSent;

    private async void CheckAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var config = _configProvider();
            if (!config.ReportEnabled)
            {
                return;
            }

            if (!TimeOnly.TryParse(config.ReportTime, out var reportTime))
            {
                return;
            }

            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            if (TimeOnly.FromDateTime(now) < reportTime)
            {
                return;
            }

            if (!IsScheduledDay(config, today))
            {
                return;
            }

            var isMonthly = IsMonthly(config);
            var periodStart = isMonthly
                ? new DateOnly(today.Year, today.Month, 1)
                : StartOfWeek(today);
            var periodKey = BuildPeriodKey(periodStart, isMonthly);

            lock (_stateLock)
            {
                if (periodKey == _lastPeriodKey)
                {
                    return;
                }

                if (_attemptPeriodKey != periodKey)
                {
                    // 进入新的发送周期，重置失败计数；否则上个周期连续失败 5 次后，
                    // 后续所有周期都会被同一计数器卡死。
                    _attemptPeriodKey = periodKey;
                    _failedAttempts = 0;
                }

                if (_failedAttempts >= MaxAttemptsPerPeriod)
                {
                    // 这个周期重试太多次了，多半是 webhook 配错；放弃，下个周期再来
                    return;
                }
            }

            var report = TaskReportBuilder.Build(_data, _syncRoot, periodStart, today, isMonthly);
            var text = TaskReportBuilder.RenderText(report);
            var markdown = TaskReportBuilder.RenderMarkdown(report);

            var channels = BuildChannels(config);
            if (channels.Count == 0)
            {
                // 没配任何渠道：不要标记本周期已处理，否则当天稍后补上 webhook
                // 也不会再发送；没有渠道时这里直接返回，等用户配置后再推送。
                return;
            }

            var anySent = false;
            var errors = new List<string>();
            foreach (var channel in channels)
            {
                try
                {
                    await channel.SendAsync(text, markdown);
                    anySent = true;
                }
                catch (Exception ex)
                {
                    errors.Add($"{channel.Name}: {ex.Message}");
                }
            }

            if (anySent)
            {
                MarkSent(periodKey);
                ReportSent?.Invoke(text);
                if (errors.Count > 0)
                {
                    App.LogError(new InvalidOperationException(string.Join(" | ", errors)), "ReportService(部分渠道失败)");
                }
            }
            else
            {
                lock (_stateLock)
                {
                    _failedAttempts++;
                }

                App.LogError(
                    new InvalidOperationException($"报告推送全部失败（第 {_failedAttempts} 次）：{string.Join(" | ", errors)}"),
                    "ReportService");
            }
        }
        catch (Exception ex)
        {
            // 定时器回调是 async void（线程池），异常会终止进程，必须在此兜底
            App.LogError(ex, "ReportService");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>手动立即发送一次（设置面板的"立即发送"按钮）。返回 (是否成功, 说明)。</summary>
    public async Task<(bool Sent, string Message)> RunOnceAsync()
    {
        var config = _configProvider();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var isMonthly = IsMonthly(config);
        var periodStart = isMonthly
            ? new DateOnly(today.Year, today.Month, 1)
            : StartOfWeek(today);

        var report = TaskReportBuilder.Build(_data, _syncRoot, periodStart, today, isMonthly);
        var text = TaskReportBuilder.RenderText(report);
        var markdown = TaskReportBuilder.RenderMarkdown(report);

        var channels = BuildChannels(config);
        if (channels.Count == 0)
        {
            return (false, "没有配置任何推送渠道：请填写飞书 / 企业微信 webhook 并勾选对应开关。");
        }

        var sent = new List<string>();
        var errors = new List<string>();
        foreach (var channel in channels)
        {
            try
            {
                await channel.SendAsync(text, markdown);
                sent.Add(channel.Name);
            }
            catch (Exception ex)
            {
                errors.Add($"{channel.Name}：{ex.Message}");
            }
        }

        if (sent.Count == 0)
        {
            return (false, "推送失败：\n" + string.Join("\n", errors));
        }

        return (true, errors.Count == 0
            ? $"已推送到：{string.Join("、", sent)}"
            : $"已推送到：{string.Join("、", sent)}\n以下渠道失败：\n{string.Join("\n", errors)}");
    }

    /// <summary>预览当前周期的报告文本（不推送）。</summary>
    public string Preview()
    {
        var config = _configProvider();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var isMonthly = IsMonthly(config);
        var periodStart = isMonthly
            ? new DateOnly(today.Year, today.Month, 1)
            : StartOfWeek(today);
        var report = TaskReportBuilder.Build(_data, _syncRoot, periodStart, today, isMonthly);
        return TaskReportBuilder.RenderText(report);
    }

    private List<Channel> BuildChannels(AppConfig config)
    {
        var channels = new List<Channel>();

        if (config.ReportSendToFeishu && !string.IsNullOrWhiteSpace(config.FeishuWebhook))
        {
            var hook = config.FeishuWebhook.Trim();
            channels.Add(new Channel("飞书", (_, markdown) => _sender.SendFeishuCardAsync(hook, "任务完成情况", markdown)));
        }

        if (config.ReportSendToWeCom && !string.IsNullOrWhiteSpace(config.WeComWebhook))
        {
            var hook = config.WeComWebhook.Trim();
            channels.Add(new Channel("企业微信", (_, markdown) => _sender.SendWeComMarkdownAsync(hook, markdown)));
        }

        if (!string.IsNullOrWhiteSpace(config.ReportCustomWebhook))
        {
            var hook = config.ReportCustomWebhook.Trim();
            channels.Add(new Channel("自定义 webhook", (text, markdown) => _sender.SendCustomTextAsync(hook, text, markdown)));
        }

        return channels;
    }

    private static bool IsMonthly(AppConfig config) =>
        string.Equals(config.ReportSchedule, "Monthly", StringComparison.OrdinalIgnoreCase);

    /// <summary>今天是不是该发报告的日子。</summary>
    private static bool IsScheduledDay(AppConfig config, DateOnly today)
    {
        if (IsMonthly(config))
        {
            var lastDay = DateTime.DaysInMonth(today.Year, today.Month);
            var target = Math.Clamp(config.ReportDayOfMonth, 1, lastDay);
            return today.Day == target;
        }

        // 1=周一 … 7=周日  ->  .NET DayOfWeek: 0=周日 … 6=周六
        var targetDay = Math.Clamp(config.ReportDayOfWeek, 1, 7);
        var todayNumber = today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek;
        return todayNumber == targetDay;
    }

    /// <summary>取今天所在周的周一。</summary>
    public static DateOnly StartOfWeek(DateOnly date)
    {
        var diff = date.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)date.DayOfWeek - 1;
        return date.AddDays(-diff);
    }

    private static string BuildPeriodKey(DateOnly periodStart, bool isMonthly)
    {
        if (isMonthly)
        {
            return periodStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }

        var week = ISOWeek.GetWeekOfYear(periodStart.ToDateTime(TimeOnly.MinValue));
        return $"{periodStart.Year}-W{week:00}";
    }

    private void MarkSent(string periodKey)
    {
        lock (_stateLock)
        {
            _lastPeriodKey = periodKey;
            _attemptPeriodKey = periodKey;
            _failedAttempts = 0;
        }

        SaveState();
    }

    private void LoadState()
    {
        try
        {
            var path = GetStatePath();
            if (!File.Exists(path))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("lastPeriodKey", out var el))
            {
                lock (_stateLock)
                {
                    _lastPeriodKey = el.GetString() ?? string.Empty;
                }
            }
        }
        catch
        {
            // 状态文件损坏就当没发过，最坏情况是多发一次，优于永远不发
        }
    }

    private void SaveState()
    {
        try
        {
            var path = GetStatePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string key;
            lock (_stateLock)
            {
                key = _lastPeriodKey;
            }

            File.WriteAllText(path, JsonSerializer.Serialize(new { lastPeriodKey = key }, StateJsonOptions));
        }
        catch
        {
            // 忽略写入失败：状态只用于防重复推送，写不进去不影响主流程
        }
    }

    private static string GetStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MicaAgenda", "report-state.json");
    }

    public void Dispose()
    {
        _timer.Dispose();
        _sender.Dispose();
    }

    private sealed record Channel(string Name, Func<string, string, Task> SendAsync);
}
