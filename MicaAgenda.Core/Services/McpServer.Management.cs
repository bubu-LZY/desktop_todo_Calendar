using System.Text.Json;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// McpServer 的「管理类」工具实现：周期任务系列的查 / 改 / 删全部、报告发送与预览、
/// 立即备份、清除已完成任务。
///
/// 拆分文件是为了让 McpServer.cs 保持在可读长度内（工具定义与分发仍在那边，实现在这里）。
/// 需要宿主服务的部分统一走 <see cref="McpHostActions"/> 注入 —— 包括 AI 内部那个
/// 不监听端口的 McpServer 实例，所以这些工具在 AI 对话里同样可用。
/// </summary>
public sealed partial class McpServer
{
    // ===== 周期任务系列管理 =====

    /// <summary>列出所有周期任务系列（源任务），供 AI / MCP 管理。</summary>
    private object ListRecurringSeries()
    {
        lock (_syncRoot)
        {
            var masters = _data.Tasks
                .Where(task => task.Recurrence != RecurrenceFrequency.None)
                .OrderBy(task => task.Date)
                .ThenBy(task => task.Title, StringComparer.Ordinal)
                .ToList();

            var series = masters.Select(master => new
            {
                seriesId = master.Id,
                title = master.Title,
                rule = RecurrenceService.DescribeRule(master),
                frequency = master.Recurrence.ToString().ToLowerInvariant(),
                interval = master.RecurrenceInterval,
                startDate = master.Date.ToString("yyyy-MM-dd"),
                endDate = master.RecurrenceEnd?.ToString("yyyy-MM-dd"),
                time = master.Time?.ToString("HH:mm"),
                reminders = ReminderLeadCatalog.ToLabels(master.AllReminderLeads),
                instanceCount = _data.Tasks.Count(task => task.SeriesId == master.Id)
            }).ToList();

            return new { count = series.Count, series };
        }
    }

    /// <summary>
    /// 修改一个周期任务系列：更新源任务规则后，删掉旧物化实例并按新规则重新铺开。
    /// 只改传入的字段；reminders 一旦传入即整组替换（[] = 不提醒）。
    /// </summary>
    private object UpdateRecurringSeries(Guid id, JsonElement args)
    {
        int removedInstances;
        int createdInstances;

        lock (_syncRoot)
        {
            var master = _data.Tasks.FirstOrDefault(t => t.Id == id && t.Recurrence != RecurrenceFrequency.None)
                ?? throw new KeyNotFoundException($"recurring series not found: {id}");

            if (TryGetArg(args, "title", out var titleEl) && !string.IsNullOrWhiteSpace(titleEl.GetString()))
            {
                master.Title = titleEl.GetString()!.Trim();
            }

            if (TryGetArg(args, "frequency", out var freqEl) && freqEl.ValueKind == JsonValueKind.String)
            {
                master.Recurrence = ParseFrequencyText(freqEl.GetString());
            }

            if (TryGetArg(args, "interval", out var intervalEl) && intervalEl.ValueKind == JsonValueKind.Number)
            {
                master.RecurrenceInterval = Math.Max(1, intervalEl.GetInt32());
            }

            // end：传了就改（null / 空串 = 取消结束限制，按物化封顶铺开）
            if (TryGetArg(args, "end", out var endEl))
            {
                master.RecurrenceEnd = ParseDateElement(endEl);
            }

            if (TryGetArg(args, "time", out var timeEl))
            {
                master.Time = NormalizeTaskTime(ParseTimeElement(timeEl));
            }

            if (TryGetArg(args, "reminders", out var reminderEl))
            {
                master.SetReminderLeads(ParseReminderLeadsElement(reminderEl));
            }

            master.ResetReminder();
            master.SuppressMissedLeadReminders(DateTimeOffset.Now);

            // 物化架构下「改规则」= 重建：删旧实例（保留源任务本身）→ 按新规则重新展开。
            removedInstances = _data.Tasks.RemoveAll(t => t.SeriesId == id);

            var instances = RecurrenceService.Expand(master);
            foreach (var instance in instances)
            {
                instance.SuppressMissedLeadReminders(DateTimeOffset.Now);
            }

            _data.Tasks.AddRange(instances);
            createdInstances = instances.Count;
        }

        _onDataChanged();
        return new { seriesId = id, removedInstances, createdInstances };
    }

    /// <summary>删除所有周期任务系列（源任务 + 全部物化实例）。</summary>
    private object DeleteAllRecurringSeries()
    {
        int seriesCount;
        lock (_syncRoot)
        {
            var masterIds = _data.Tasks
                .Where(task => task.Recurrence != RecurrenceFrequency.None)
                .Select(task => task.Id)
                .ToHashSet();
            seriesCount = masterIds.Count;

            _data.Tasks.RemoveAll(task =>
                task.Recurrence != RecurrenceFrequency.None
                || (task.SeriesId is { } sid && masterIds.Contains(sid)));
        }

        if (seriesCount > 0)
        {
            _onDataChanged();
        }

        return new { removedSeries = seriesCount };
    }

    // ===== 报告 / 备份（需宿主注入 McpHostActions）=====

    /// <summary>立即把任务完成报告推送到已配置的渠道（飞书卡片 / 企微 markdown / 自定义）。</summary>
    private object SendReport()
    {
        var send = RequireHostActions().SendReportAsync
            ?? throw new InvalidOperationException("报告能力未就绪：宿主未注入发送报告的实现。");

        var (sent, message) = send().GetAwaiter().GetResult();
        return new { sent, message };
    }

    /// <summary>预览当前周期的报告文本（不发送）。</summary>
    private object PreviewReport()
    {
        var preview = RequireHostActions().PreviewReport
            ?? throw new InvalidOperationException("报告能力未就绪：宿主未注入预览报告的实现。");

        return new { report = preview() };
    }

    /// <summary>立即执行一次备份（导出今年任务 JSON，并按配置推送飞书 / 企微）。</summary>
    private object RunBackup()
    {
        var run = RequireHostActions().RunBackupAsync
            ?? throw new InvalidOperationException("备份能力未就绪：宿主未注入执行备份的实现。");

        return new { message = run().GetAwaiter().GetResult() };
    }

    // ===== 清理 =====

    /// <summary>删除所有已完成任务。返回删除条数。</summary>
    private object ClearCompletedTasks()
    {
        int removed;
        lock (_syncRoot)
        {
            removed = _data.Tasks.RemoveAll(task => task.IsCompleted);
        }

        if (removed > 0)
        {
            _onDataChanged();
        }

        return new { removed };
    }

    private McpHostActions RequireHostActions()
        => _hostActions
           ?? throw new InvalidOperationException("宿主能力未注入：该工具需要宿主提供报告 / 备份支持。");

    /// <summary>解析可选的重复频率（不传就不动，传了非法值直接报错让 AI 自我修正）。</summary>
    private static RecurrenceFrequency ParseFrequencyText(string? text)
        => text?.Trim().ToLowerInvariant() switch
        {
            "daily" => RecurrenceFrequency.Daily,
            "weekly" => RecurrenceFrequency.Weekly,
            "monthly" => RecurrenceFrequency.Monthly,
            "yearly" => RecurrenceFrequency.Yearly,
            _ => throw new ArgumentException(
                $"unknown frequency: \"{text}\"; valid: daily, weekly, monthly, yearly")
        };

    /// <summary>解析日期元素：JSON null / 空串 = 清除（null），字符串必须是 YYYY-MM-DD。</summary>
    private static DateOnly? ParseDateElement(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("date must be a string in YYYY-MM-DD format");
        }

        var text = el.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (DateOnly.TryParse(text, out var date))
        {
            return date;
        }

        throw new ArgumentException($"invalid date: \"{text}\"; expected YYYY-MM-DD");
    }
}
