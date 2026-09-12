using System.Text;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>报告里的一行任务（已把时间指标算好，渲染层只负责排版）。</summary>
public sealed class ReportTaskLine
{
    public string Title { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public TimeSpan? Duration { get; set; }
    public int PendingDays { get; set; }
    public int OverdueDays { get; set; }
    public int LateDays { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string DurationText => Duration is null
        ? "用时未知"
        : TimeText.FormatDuration(Duration.Value);
}

/// <summary>一次统计周期的任务完成情况快照。</summary>
public sealed class TaskReport
{
    public bool IsMonthly { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>期间内新建的任务数。</summary>
    public int CreatedCount { get; set; }

    /// <summary>期间内实际完成的任务数（含补做的历史欠账）。</summary>
    public int CompletedCount { get; set; }

    /// <summary>期间内到期的任务数。</summary>
    public int DueCount { get; set; }

    /// <summary>期间内到期且已完成的任务数。</summary>
    public int DueCompletedCount { get; set; }

    /// <summary>当前仍未完成的任务总数（不限周期，即"历史欠账"全量）。</summary>
    public int OpenCount { get; set; }

    /// <summary>当前仍未完成且已过计划日期的任务数。</summary>
    public int OverdueCount { get; set; }

    public TimeSpan? AverageDuration { get; set; }

    /// <summary>期间完成率（期间到期任务里已完成的比例），0~1。</summary>
    public double DueCompletionRate => DueCount == 0 ? 0 : (double)DueCompletedCount / DueCount;

    /// <summary>耗时最长的已完成项。</summary>
    public List<ReportTaskLine> Longest { get; } = new();

    /// <summary>完成得最快的一批。</summary>
    public List<ReportTaskLine> Fastest { get; } = new();

    /// <summary>超过计划日期才完成的（超时完成）。</summary>
    public List<ReportTaskLine> LateCompleted { get; } = new();

    /// <summary>至今仍未完成的（按拖了多久降序）。</summary>
    public List<ReportTaskLine> StillOpen { get; } = new();

    /// <summary>至今未完成且已逾期的。</summary>
    public List<ReportTaskLine> Overdue { get; } = new();

    public string PeriodLabel => $"{PeriodStart:yyyy-MM-dd} ~ {PeriodEnd:yyyy-MM-dd}";
    public string KindLabel => IsMonthly ? "月报" : "周报";
}

/// <summary>
/// 生成任务完成情况报告：耗时最长 / 快速完成 / 超时完成 / 至今未完成 / 逾期未完成。
/// 只读数据，不修改任何任务。
/// </summary>
public static class TaskReportBuilder
{
    private const int TopCount = 3;
    private const int OpenLimit = 10;

    public static TaskReport Build(
        CalendarData data,
        object syncRoot,
        DateOnly periodStart,
        DateOnly today,
        bool isMonthly)
    {
        List<CalendarTask> snapshot;
        lock (syncRoot)
        {
            snapshot = data.Tasks.ToList();
        }

        var now = DateTimeOffset.Now;
        var report = new TaskReport
        {
            IsMonthly = isMonthly,
            PeriodStart = periodStart,
            PeriodEnd = today
        };

        // 期间内完成的：以 CompletedAt 落在区间内为准（含补做的旧任务）
        var completedInPeriod = snapshot
            .Where(t => t.IsCompleted && t.CompletedAt is { } at
                        && DateOnly.FromDateTime(at.LocalDateTime) >= periodStart
                        && DateOnly.FromDateTime(at.LocalDateTime) <= today)
            .ToList();

        var dueInPeriod = snapshot
            .Where(t => t.Date >= periodStart && t.Date <= today)
            .ToList();

        report.CompletedCount = completedInPeriod.Count;
        report.DueCount = dueInPeriod.Count;
        report.DueCompletedCount = dueInPeriod.Count(t => t.IsCompleted);
        report.CreatedCount = snapshot.Count(t => t.CreatedDate >= periodStart && t.CreatedDate <= today);

        // 耗时排名：只有拿到 CompletedAt 的才排得进去，缺时间戳的归入 LateCompleted 等分组但不参与均值
        var timed = completedInPeriod
            .Where(t => t.GetCompletionDuration() is not null)
            .OrderByDescending(t => t.GetCompletionDuration()!.Value)
            .ToList();

        report.Longest.AddRange(timed.Take(TopCount).Select(t => ToLine(t, today)));
        report.Fastest.AddRange(timed.AsEnumerable().Reverse().Take(TopCount).Select(t => ToLine(t, today)));

        report.LateCompleted.AddRange(completedInPeriod
            .Where(t => t.IsCompletedLate())
            .OrderByDescending(t => t.GetCompletedLateDays())
            .ThenBy(t => t.Date)
            .Take(OpenLimit)
            .Select(t => ToLine(t, today)));

        var openTasks = snapshot
            .Where(t => !t.IsCompleted)
            .OrderByDescending(t => t.GetPendingDays(today))
            .ThenBy(t => t.Date)
            .ToList();

        report.OpenCount = openTasks.Count;
        report.OverdueCount = openTasks.Count(t => t.IsOverdue(today));

        report.StillOpen.AddRange(openTasks.Take(OpenLimit).Select(t => ToLine(t, today)));
        report.Overdue.AddRange(openTasks
            .Where(t => t.IsOverdue(today))
            .OrderByDescending(t => t.GetOverdueDays(today))
            .ThenBy(t => t.Date)
            .Take(OpenLimit)
            .Select(t => ToLine(t, today)));

        report.AverageDuration = timed.Count == 0
            ? null
            : TimeSpan.FromTicks((long)timed
                .Select(t => t.GetCompletionDuration()!.Value.Ticks)
                .Average());

        return report;
    }

    private static ReportTaskLine ToLine(CalendarTask task, DateOnly today) => new()
    {
        Title = task.Title,
        Date = task.Date,
        Duration = task.GetCompletionDuration(),
        PendingDays = task.GetPendingDays(today),
        OverdueDays = task.GetOverdueDays(today),
        LateDays = task.GetCompletedLateDays(),
        CompletedAt = task.CompletedAt
    };

    /// <summary>渲染成纯文本（飞书 text / 企微 text 都能直接发）。</summary>
    public static string RenderText(TaskReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【桌面日历 · {report.KindLabel}】{report.PeriodLabel}");
        sb.AppendLine();

        sb.AppendLine("📊 总览");
        sb.AppendLine($"期间到期 {report.DueCount} 项 · 已完成 {report.DueCompletedCount} 项"
                      + $"（{(report.DueCompletionRate * 100):F0}%） · 期间新建 {report.CreatedCount} 项");
        sb.AppendLine($"期间实际完成 {report.CompletedCount} 项（含补做历史任务）");
        sb.AppendLine(report.AverageDuration is null
            ? "平均完成用时：暂无数据"
            : $"平均完成用时：{TimeText.FormatDuration(report.AverageDuration.Value)}");
        sb.AppendLine($"至今未完成 {report.OpenCount} 项 · 其中逾期 {report.OverdueCount} 项");
        sb.AppendLine();

        if (report.Longest.Count > 0)
        {
            sb.AppendLine("🐢 耗时最长");
            AppendLines(sb, report.Longest, line =>
            {
                var late = line.LateDays > 0 ? $"（超时 {line.LateDays} 天完成）" : string.Empty;
                return $"{line.DurationText}{late}";
            });
            sb.AppendLine();
        }

        if (report.Fastest.Count > 0)
        {
            sb.AppendLine("⚡ 完成最快");
            AppendLines(sb, report.Fastest, line => line.DurationText);
            sb.AppendLine();
        }

        if (report.LateCompleted.Count > 0)
        {
            sb.AppendLine($"⏰ 超时完成（{report.LateCompleted.Count} 项）");
            AppendLines(sb, report.LateCompleted, line =>
                line.CompletedAt is { } at
                    ? $"计划 {line.Date:MM/dd}，实际 {at:MM/dd} 完成（超 {line.LateDays} 天）"
                    : $"计划 {line.Date:MM/dd}（超 {line.LateDays} 天）");
            sb.AppendLine();
        }

        if (report.StillOpen.Count > 0)
        {
            sb.AppendLine($"📌 至今未完成（{report.OpenCount} 项，按拖延时长排序）");
            AppendLines(sb, report.StillOpen, line =>
            {
                var pending = line.PendingDays > 0 ? $"已拖 {line.PendingDays} 天" : "当天新建";
                var overdue = line.OverdueDays > 0 ? $" · 逾期 {line.OverdueDays} 天" : string.Empty;
                return $"{pending}{overdue}";
            });
            sb.AppendLine();
        }

        if (report.Overdue.Count > 0)
        {
            sb.AppendLine($"🚨 逾期未完成（{report.OverdueCount} 项）");
            AppendLines(sb, report.Overdue, line => $"计划 {line.Date:MM/dd} · 逾期 {line.OverdueDays} 天");
        }

        if (report.CompletedCount == 0 && report.OpenCount == 0)
        {
            sb.AppendLine("本周期内没有任何任务记录。");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>渲染成企微 markdown（在企微里排版更清晰）。</summary>
    public static string RenderMarkdown(TaskReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 桌面日历 · {report.KindLabel}");
        sb.AppendLine($"**统计区间**：{report.PeriodLabel}");
        sb.AppendLine();
        sb.AppendLine("## 📊 总览");
        sb.AppendLine($"> 期间到期 **{report.DueCount}** 项，已完成 **{report.DueCompletedCount}** 项"
                      + $"（**{(report.DueCompletionRate * 100):F0}%**），期间新建 **{report.CreatedCount}** 项");

        if (report.AverageDuration is { } avg)
        {
            sb.AppendLine($"> 平均完成用时：**{TimeText.FormatDuration(avg)}**");
        }

        sb.AppendLine($"> 至今未完成 **{report.OpenCount}** 项，其中逾期 **{report.OverdueCount}** 项");
        sb.AppendLine();

        if (report.Longest.Count > 0)
        {
            sb.AppendLine("## 🐢 耗时最长");
            foreach (var line in report.Longest)
            {
                var late = line.LateDays > 0 ? $"（超时 {line.LateDays} 天完成）" : string.Empty;
                sb.AppendLine($"- {Escape(line.Title)} — **{line.DurationText}**{late}");
            }
            sb.AppendLine();
        }

        if (report.Fastest.Count > 0)
        {
            sb.AppendLine("## ⚡ 完成最快");
            foreach (var line in report.Fastest)
            {
                sb.AppendLine($"- {Escape(line.Title)} — **{line.DurationText}**");
            }
            sb.AppendLine();
        }

        if (report.LateCompleted.Count > 0)
        {
            sb.AppendLine($"## ⏰ 超时完成（{report.LateCompleted.Count}）");
            foreach (var line in report.LateCompleted)
            {
                var actual = line.CompletedAt is { } at ? at.ToString("MM/dd") : "—";
                sb.AppendLine($"- {Escape(line.Title)} — 计划 {line.Date:MM/dd}，实际 {actual}（超 {line.LateDays} 天）");
            }
            sb.AppendLine();
        }

        if (report.StillOpen.Count > 0)
        {
            sb.AppendLine($"## 📌 至今未完成（{report.OpenCount}）");
            foreach (var line in report.StillOpen)
            {
                var pending = line.PendingDays > 0 ? $"已拖 {line.PendingDays} 天" : "当天新建";
                var overdue = line.OverdueDays > 0 ? $"，逾期 {line.OverdueDays} 天" : string.Empty;
                sb.AppendLine($"- {Escape(line.Title)}（{line.Date:MM/dd}） — {pending}{overdue}");
            }
            sb.AppendLine();
        }

        if (report.Overdue.Count > 0)
        {
            sb.AppendLine($"## 🚨 逾期未完成（{report.OverdueCount}）");
            foreach (var line in report.Overdue)
            {
                sb.AppendLine($"- <font color=\"warning\">{Escape(line.Title)}</font> — 计划 {line.Date:MM/dd}，逾期 {line.OverdueDays} 天");
            }
        }

        if (report.CompletedCount == 0 && report.OpenCount == 0)
        {
            sb.AppendLine("本周期内没有任何任务记录。");
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendLines(StringBuilder sb, List<ReportTaskLine> lines, Func<ReportTaskLine, string> detail)
    {
        var index = 1;
        foreach (var line in lines)
        {
            sb.AppendLine($"{index}. {line.Title} — {detail(line)}");
            index++;
        }
    }

    /// <summary>企微 markdown 里部分字符需要转义，避免标题里的符号破坏排版。</summary>
    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(无标题)";
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("[", "\\[")
            .Replace("]", "\\]");
    }
}
