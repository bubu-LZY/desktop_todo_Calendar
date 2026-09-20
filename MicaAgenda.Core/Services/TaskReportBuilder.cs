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

    /// <summary>
    /// 从**创建**那天算起的未完成天数。注意：这是"这个任务挂了多久没动"，
    /// 与"拖了几天"不是一回事 —— 见 <see cref="DelayText"/> 里的说明。
    /// </summary>
    public int PendingDays { get; set; }

    /// <summary>已逾期天数（任务日期早于今天且未完成），未逾期为 0。</summary>
    public int OverdueDays { get; set; }

    /// <summary>
    /// 任务**到期日**相对今天的偏移：<c>&gt;0</c> 已过期几天、<c>&lt;0</c> 还有几天到期、<c>0</c> 今天到期。
    ///
    /// <para>这才是"拖了几天"的口径 —— 用户明确要求：<b>按任务自己的日期算，不是按创建时间算</b>。
    /// 旧实现用 <see cref="PendingDays"/>（创建时间）来写"已拖 N 天"，
    /// 于是"下周三才到期、今天刚建"的任务会被写成"当天新建"，而"两个月后到期、两天前建"的会被写成
    /// "已拖 2 天" —— 报告自己都写着"其中逾期 0 项"，两句话直接打架。</para>
    /// </summary>
    public int DueOffsetDays { get; set; }

    public int LateDays { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string DurationText => Duration is null
        ? "用时未知"
        : TimeText.FormatDuration(Duration.Value);

    /// <summary>
    /// 「拖延 / 到期」措辞。**全项目唯一一份定义**，三处渲染（纯文本 / 企微 markdown / 飞书 lark_md）
    /// 与两个宿主的统计窗口都读这里，避免同一个口径被复制成多份后各自漂移。
    ///
    /// <para>口径与提醒一致：跨过任务当日的 24:00 才算逾期，所以"今天到期"的任务不算拖。</para>
    /// </summary>
    public string DelayText => DueOffsetDays switch
    {
        > 0 => $"已拖 {DueOffsetDays} 天",
        < 0 => $"还有 {-DueOffsetDays} 天到期",
        _ => "今天到期"
    };
}

/// <summary>
/// 报告正文的渲染方言。同名的一份正文要发往语法不同的两个渠道，
/// 这是"卡片里露出 <c>#</c> / <c>&gt;</c> / <c>-</c>"的根因。
/// </summary>
public enum ReportDialect
{
    /// <summary>
    /// 企业微信 markdown：支持 <c>#</c> 标题 / <c>&gt;</c> 引用 / <c>-</c> 无序列表 / <c>1.</c> 有序列表。
    /// </summary>
    WeCom,

    /// <summary>
    /// 飞书卡片 <c>lark_md</c>：是 markdown 的一个**很小的子集**，只认
    /// <c>**加粗**</c>、<c>~~删除线~~</c>、<c>[文字](链接)</c>、<c>&lt;at&gt;</c> 与换行。
    ///
    /// <para><b>它不认块级语法</b> —— <c>#</c> 标题、<c>&gt;</c> 引用、<c>-</c> 列表、<c>---</c> 分隔线
    /// 都会原样显示成字符。用户截图里「<c># 桌面日历 · 周报</c>」「<c>&gt; 期间到期</c>」「<c>- 考前练车</c>」
    /// 就是把这个子集当通用 markdown 用导致的。</para>
    /// </summary>
    LarkMd
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

    /// <summary>至今仍未完成的（按到期日升序，最紧急的在前）。</summary>
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
            // 按**到期日**升序：最该处理的排最前（拖最久/最先到期），远期任务自然沉底。
            // 旧实现按"创建天数"排，会把"两个月后到期"的任务排到"昨天就该做完"的前面。
            .OrderBy(t => t.Date)
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
        // 「拖了几天」按任务自己的日期算；与 GetOverdueDays 同源，保证两处不会各说各话。
        DueOffsetDays = today.DayNumber - task.Date.DayNumber,
        LateDays = task.GetCompletedLateDays(),
        CompletedAt = task.CompletedAt
    };

    /// <summary>
    /// 渲染成纯文本（飞书 text / 企微 text 都能直接发）。
    ///
    /// <para><b>它为什么没有和 <see cref="BuildBody"/> 合并</b>：纯文本是第三种媒介，
    /// 排版规则确实不同 —— 标题用 <c>【】</c> 而不是 <c>#</c>、列表要带序号、
    /// 而且不能出现任何 <c>**</c> 记号（text 类型不渲染）。合并会改变 自定义 webhook
    /// 现有的输出格式，属于无谓的行为变更。</para>
    ///
    /// <para><b>唯一的硬要求</b>：「拖了几天 / 还有几天到期」这类措辞一律读
    /// <see cref="ReportTaskLine.DelayText"/>，不许在本方法里再拼一遍 —— 口径必须只有一份。</para>
    /// </summary>
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
            sb.AppendLine($"📌 至今未完成（{report.OpenCount} 项，按到期日排序）");
            AppendLines(sb, report.StillOpen, line =>
                line.OverdueDays > 0 ? $"{line.DelayText}（计划 {line.Date:MM/dd}）" : line.DelayText);
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

    /// <summary>渲染成企微 markdown（企微支持 # 标题 / &gt; 引用 / - 列表，排版更清晰）。</summary>
    public static string RenderMarkdown(TaskReport report) => Render(report, ReportDialect.WeCom);

    /// <summary>
    /// 渲染成飞书卡片正文。飞书走 <c>lark_md</c>，那是 markdown 的一个**很小的子集**，
    /// 块级语法（<c>#</c> / <c>&gt;</c> / <c>-</c>）不认、会原样显示 —— 详见 <see cref="ReportDialect.LarkMd"/>。
    /// </summary>
    public static string RenderFeishuMarkdown(TaskReport report) => Render(report, ReportDialect.LarkMd);

    /// <summary>按指定方言渲染报告正文。</summary>
    public static string Render(TaskReport report, ReportDialect dialect)
        => Emit(BuildBody(report, dialect), dialect);

    /// <summary>
    /// 正文的一个块。<b>内容与语法分离</b>：块只声明"这是一级标题 / 引用 / 列表项"，
    /// 具体记号由 <see cref="Emit"/> 按方言翻译。
    ///
    /// <para>这样做的理由是上一版的真实教训：当时只有一份 markdown 字符串，
    /// 同一条字符串直接喂给企微和飞书 —— 加内容的人根本无从察觉飞书不认 <c>#</c> / <c>&gt;</c> / <c>-</c>，
    /// 于是卡片里满屏记号。把"内容"与"语法"分开之后，两条渠道的**正文内容只可能来自同一个
    /// <see cref="BuildBody"/>**，差异被压缩到 <see cref="Emit"/> 一处，加内容不会再踩这个坑。</para>
    /// </summary>
    private abstract record Block;

    private sealed record Heading(int Level, string Text) : Block;

    private sealed record Quote(string Text) : Block;

    private sealed record Bullet(string Text) : Block;

    private sealed record Paragraph(string Text) : Block;

    private sealed record Gap : Block;

    /// <summary>
    /// 按方言把块翻译成文本。这是两种渠道**唯一**允许不同的地方。
    /// </summary>
    private static string Emit(IReadOnlyList<Block> blocks, ReportDialect dialect)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Gap:
                    sb.AppendLine();
                    break;

                case Heading heading:
                    if (dialect == ReportDialect.LarkMd)
                    {
                        // lark_md 不认 #；但它认 **，用整行加粗表达层级。
                        sb.AppendLine($"**{heading.Text}**");
                    }
                    else
                    {
                        sb.AppendLine($"{new string('#', heading.Level)} {heading.Text}");
                    }

                    break;

                case Quote quote:
                    // 飞书没有引用块，退化成普通行；内容里本来就有 ** 强调，层级不至于全丢。
                    sb.AppendLine(dialect == ReportDialect.LarkMd ? quote.Text : $"> {quote.Text}");
                    break;

                case Bullet bullet:
                    // 飞书不认 "- "，改用项目符号字符 —— 它是普通文本，不依赖任何渲染。
                    sb.AppendLine(dialect == ReportDialect.LarkMd ? $"• {bullet.Text}" : $"- {bullet.Text}");
                    break;

                case Paragraph paragraph:
                    sb.AppendLine(paragraph.Text);
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 组装报告正文的块序列 —— **两种 markdown 渠道共用的唯一内容来源**。
    /// </summary>
    private static List<Block> BuildBody(TaskReport report, ReportDialect dialect)
    {
        var blocks = new List<Block>
        {
            new Heading(1, $"桌面日历 · {report.KindLabel}"),
            new Paragraph($"**统计区间**：{report.PeriodLabel}"),
            new Gap(),
            new Heading(2, "📊 总览"),
            new Quote($"期间到期 **{report.DueCount}** 项，已完成 **{report.DueCompletedCount}** 项"
                      + $"（**{(report.DueCompletionRate * 100):F0}%**），期间新建 **{report.CreatedCount}** 项")
        };

        if (report.AverageDuration is { } avg)
        {
            blocks.Add(new Quote($"平均完成用时：**{TimeText.FormatDuration(avg)}**"));
        }

        blocks.Add(new Quote($"至今未完成 **{report.OpenCount}** 项，其中逾期 **{report.OverdueCount}** 项"));
        blocks.Add(new Gap());

        if (report.Longest.Count > 0)
        {
            blocks.Add(new Heading(2, "🐢 耗时最长"));
            foreach (var line in report.Longest)
            {
                var late = line.LateDays > 0 ? $"（超时 {line.LateDays} 天完成）" : string.Empty;
                blocks.Add(new Bullet($"{SafeTitle(line.Title, dialect)} — **{line.DurationText}**{late}"));
            }

            blocks.Add(new Gap());
        }

        if (report.Fastest.Count > 0)
        {
            blocks.Add(new Heading(2, "⚡ 完成最快"));
            foreach (var line in report.Fastest)
            {
                blocks.Add(new Bullet($"{SafeTitle(line.Title, dialect)} — **{line.DurationText}**"));
            }

            blocks.Add(new Gap());
        }

        if (report.LateCompleted.Count > 0)
        {
            blocks.Add(new Heading(2, $"⏰ 超时完成（{report.LateCompleted.Count}）"));
            foreach (var line in report.LateCompleted)
            {
                var actual = line.CompletedAt is { } at ? at.ToString("MM/dd") : "—";
                blocks.Add(new Bullet(
                    $"{SafeTitle(line.Title, dialect)} — 计划 {line.Date:MM/dd}，实际 {actual}（超 {line.LateDays} 天）"));
            }

            blocks.Add(new Gap());
        }

        if (report.StillOpen.Count > 0)
        {
            blocks.Add(new Heading(2, $"📌 至今未完成（{report.OpenCount}）"));
            foreach (var line in report.StillOpen)
            {
                // 「拖了几天」一律走 DelayText：以**任务自己的日期**为准。
                // 注意这里**不再**补一句"，逾期 N 天" —— 两个数同源，并排写只是把同一件事说两遍。
                blocks.Add(new Bullet($"{SafeTitle(line.Title, dialect)}（{line.Date:MM/dd}） — {line.DelayText}"));
            }

            blocks.Add(new Gap());
        }

        if (report.Overdue.Count > 0)
        {
            blocks.Add(new Heading(2, $"🚨 逾期未完成（{report.OverdueCount}）"));
            foreach (var line in report.Overdue)
            {
                // 原来这里用 <font color="warning"> 想标红，但飞书 lark_md 并不支持 <font> 标签 ——
                // 原样发出去用户看到的就是一串标签文本。改用 ** 加粗，两种渠道都认。
                blocks.Add(new Bullet(
                    $"**{SafeTitle(line.Title, dialect)}** — 计划 {line.Date:MM/dd}，逾期 {line.OverdueDays} 天"));
            }
        }

        if (report.CompletedCount == 0 && report.OpenCount == 0)
        {
            blocks.Add(new Paragraph("本周期内没有任何任务记录。"));
        }

        return blocks;
    }

    /// <summary>
    /// 把用户可控的标题转成该方言下的安全文本。
    ///
    /// <para>飞书侧<b>刻意不做反斜杠转义</b>：lark_md 是否认 <c>\*</c> 没有把握，赌错的代价是
    /// 把用户的标题原样加一串反斜杠。改为直接剥掉记号 —— 复用已有单测覆盖的
    /// <see cref="MarkdownText.ToSingleLine"/>，让"标题按用户打的字显示"在两种方言下都成立。</para>
    /// </summary>
    private static string SafeTitle(string title, ReportDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "(无标题)";
        }

        if (dialect == ReportDialect.WeCom)
        {
            return Escape(title);
        }

        var plain = MarkdownText.ToSingleLine(title);
        return plain.Length == 0 ? "(无标题)" : plain;
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
