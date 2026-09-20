using System.Text;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 提醒推送的文案构件（纯函数，可单测）。
///
/// <para><b>为什么单独抽出来</b>：</para>
/// <list type="number">
///   <item>文案要能测。用户明确要求推送「简洁明了、内容完整」，还要求「不要把源码样式发出去」。</item>
///   <item>推送走的是<b>纯文本</b>消息（<c>msgtype=text</c>），而任务标题支持 Markdown ——
///         直接插进去会把 <c>**粗体**</c>、<c># 标题</c>、换行这些记号原样暴露给用户，
///         还会把「1. xxx」的列表结构搞坏。所以标题一律先过
///         <see cref="MarkdownText.ToSingleLine"/>（去记号 + 压成单行）。</item>
/// </list>
///
/// <para><b>逾期口径</b>（用户明确指定）：界线是<b>任务当日的 24:00</b> —— 跨过那一天才算逾期。
/// 所以「今天」的任务即便时刻已经过了，也仍然只是当天待办，不算逾期；这也正是「逾期预警」
/// 存在的意义：在跨日之前提醒一次，别让当天没做完的任务悄无声息地变成逾期欠账。</para>
/// </summary>
public static class ReminderTextBuilder
{
    /// <summary>汇总里「已逾期」明细的时间窗口：只看最近这些天，避免积年旧账把消息撑爆。</summary>
    public const int OverdueLookbackDays = 30;

    /// <summary>已完成 / 未完成的列表前缀。</summary>
    private const string DoneMark = "✅";
    private const string PendingMark = "⬜";

    /// <summary>逾期判定：**跨过任务当日的 24:00** 才算逾期，所以只要 <c>Date == today</c> 就还不算。</summary>
    public static bool IsOverdue(CalendarTask task, DateOnly today)
        => !task.IsCompleted && task.Date < today;

    /// <summary>是否落在「已逾期」明细的时间窗口内（用于决定要不要列出来）。</summary>
    public static bool IsWithinOverdueWindow(CalendarTask task, DateOnly today)
        => IsOverdue(task, today) && task.Date >= today.AddDays(-OverdueLookbackDays);

    /// <summary>
    /// 每日汇总：今日任务清单 + 已逾期欠账。
    ///
    /// <para>今日任务<b>包含已完成的</b>（让用户看到今天的整体情况），逾期部分只列未完成的。</para>
    /// </summary>
    public static string BuildDailyDigest(
        DateOnly date,
        IReadOnlyList<CalendarTask> todayTasks,
        IReadOnlyList<CalendarTask> overdue)
    {
        var pending = todayTasks.Where(t => !t.IsCompleted).ToList();
        var done = todayTasks.Where(t => t.IsCompleted).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"【桌面日历提醒】{date:yyyy年M月d日}");

        if (todayTasks.Count == 0)
        {
            sb.AppendLine("今日没有任务安排。");
        }
        else if (pending.Count == 0)
        {
            sb.AppendLine($"今日任务共 {todayTasks.Count} 项，已全部完成。");
        }
        else
        {
            // 把逾期界线写进这一行：让用户一眼知道这些未完成项过了今天 24:00 就会被计入逾期。
            sb.AppendLine($"今日任务共 {todayTasks.Count} 项，待完成 {pending.Count} 项（过今天 24:00 未完成即算逾期）：");
        }

        AppendNumbered(sb, todayTasks
            .OrderBy(t => t.IsCompleted)
            .ThenByDescending(t => t.IsImportant)
            .ToList(),
            prefix: _ => string.Empty,
            suffix: _ => string.Empty);

        if (overdue.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"已逾期 {overdue.Count} 项（仅列最近 {OverdueLookbackDays} 天）：");
            AppendNumbered(sb, overdue
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.IsImportant)
                .ToList(),
                // 日期放在标题前：逾期清单里"哪天的"比"什么事"更先要看清。
                prefix: task => $"{task.Date.Month}月{task.Date.Day}日 ",
                suffix: _ => string.Empty);
        }

        if (done.Count > 0 && pending.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"今天已完成 {done.Count} 项。");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 逾期预警：当天还没做完、过了 24:00 就要算逾期的那几条。
    ///
    /// <para>只推当天未完成的（这才是「即将逾期」）；往期已逾期的只报一个总数，
    /// 免得每天把同一批旧账重复列一遍。</para>
    /// </summary>
    public static string BuildOverdueWarning(
        DateOnly date,
        IReadOnlyList<CalendarTask> pendingToday,
        int earlierOverdueCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【桌面日历逾期预警】{date:yyyy年M月d日}");
        sb.AppendLine($"今天还有 {pendingToday.Count} 项没完成，过了 24:00 就算逾期：");

        AppendNumbered(sb, pendingToday,
            prefix: _ => string.Empty,
            suffix: task => task.Time is { } time ? $"（{time:HH:mm}）" : string.Empty);

        if (earlierOverdueCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"另有 {earlierOverdueCount} 项往期逾期未完成。");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 单条任务某个档位的到点提醒文案。
    /// 「提前一天」专门点明是次日的任务，免得收到时以为提醒错了日子。
    /// </summary>
    public static string BuildTaskReminder(CalendarTask task, int lead)
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
        sb.AppendLine(lead > 0
            ? $"「{Title(task)}」将在 {dayText}{timeText} 开始（还有 {TimeText.FormatLead(lead)}）"
            : $"「{Title(task)}」的时间到了（{dayText}{timeText}）");

        if (task.IsImportant)
        {
            sb.AppendLine("⭐ 重要任务");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 写入带编号的清单。每行形如 <c>1. ✅ [前缀]标题 ⭐[后缀]</c>。
    /// <paramref name="prefix"/> 放"哪天的"这类限定（逾期清单用），
    /// <paramref name="suffix"/> 放行尾补充（时刻）。
    /// </summary>
    private static void AppendNumbered(
        StringBuilder sb,
        IReadOnlyList<CalendarTask> tasks,
        Func<CalendarTask, string> prefix,
        Func<CalendarTask, string> suffix)
    {
        var index = 1;
        foreach (var task in tasks)
        {
            var mark = task.IsCompleted ? DoneMark : PendingMark;
            var star = task.IsImportant ? " ⭐" : string.Empty;
            sb.AppendLine($"{index}. {mark} {prefix(task)}{Title(task)}{star}{suffix(task)}");
            index++;
        }
    }

    /// <summary>
    /// 标题：先去 Markdown 记号、再压成单行。
    /// 空标题给个占位，避免出现「1. ⬜」这种没内容的行。
    /// </summary>
    private static string Title(CalendarTask task)
    {
        var text = MarkdownText.ToSingleLine(task.Title);
        return text.Length == 0 ? "(无标题)" : text;
    }
}
