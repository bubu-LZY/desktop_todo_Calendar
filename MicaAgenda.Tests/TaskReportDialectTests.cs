using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Tests;

/// <summary>
/// 报告推送的两件事，都来自用户反馈：
///
/// <list type="number">
///   <item><b>卡片里露出 Markdown 记号</b>（<c># 桌面日历 · 周报</c> / <c>&gt; 期间到期</c> / <c>- 考前练车</c>）——
///     根因是把企微方言的正文原样喂给了飞书的 <c>lark_md</c>，而后者不认块级语法。</item>
///   <item><b>「已拖 N 天」按创建时间算</b> —— 未来才到期的任务被写成"已拖 2 天"，
///     而报告自己又写着"其中逾期 0 项"，两句话互相打架。</item>
/// </list>
///
/// 用例刻意复刻用户截图那一刻的真实数据（4 个任务、今天 = 09/20），
/// 而不是构造"理想输入" —— 上一版就是因为只测了理想输入才漏掉这个口径问题。
/// </summary>
public sealed class TaskReportDialectTests
{
    private static readonly DateOnly Today = new(2026, 9, 20);

    /// <summary>截图里的四个任务：到期日 09/21、09/22、09/22、10/24，今天 09/20，全部未完成。</summary>
    private static TaskReport ScreenshotReport(params CalendarTask[] extra)
    {
        var tasks = new List<CalendarTask>
        {
            Open("考前练车", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 18)),
            Open("模拟考试", new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 18)),
            Open("正式考试", new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 19)),
            Open("马克思-自考", new DateOnly(2026, 10, 24), new DateOnly(2026, 9, 20)),
        };

        tasks.AddRange(extra);

        var data = new CalendarData { Tasks = tasks };
        var periodStart = new DateOnly(2026, 9, 14);
        return TaskReportBuilder.Build(data, new object(), periodStart, Today, isMonthly: false);
    }

    private static CalendarTask Open(string title, DateOnly date, DateOnly createdOn) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Date = date,
        CreatedAt = new DateTimeOffset(createdOn.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(8))
    };

    // ===== 一、拖延天数：按任务日期算，不按创建时间 =====

    [Fact]
    public void DelayText_IsMeasuredAgainstTaskDate_NotCreationDate()
    {
        var report = ScreenshotReport();
        var byTitle = report.StillOpen.ToDictionary(line => line.Title);

        // 这四个任务全都**还没到期**（报告自己也写着"其中逾期 0 项"），
        // 所以一个"已拖"都不该出现 —— 旧实现在这里全部按创建时间报了 1~2 天。
        Assert.Equal(0, report.OverdueCount);
        Assert.All(report.StillOpen, line => Assert.DoesNotContain("已拖", line.DelayText));

        Assert.Equal("还有 1 天到期", byTitle["考前练车"].DelayText);
        Assert.Equal("还有 2 天到期", byTitle["模拟考试"].DelayText);
        Assert.Equal("还有 2 天到期", byTitle["正式考试"].DelayText);
        Assert.Equal("还有 34 天到期", byTitle["马克思-自考"].DelayText);
    }

    [Fact]
    public void DelayText_CrossesIntoOverdueOnlyAfterTaskDays24Hours()
    {
        // 用户口径：跨过任务当日的 24:00 才算逾期。所以"今天"不算拖、"昨天"才算拖 1 天。
        var today = new ReportTaskLine { Date = Today, DueOffsetDays = 0 };
        var yesterday = new ReportTaskLine { Date = Today.AddDays(-1), DueOffsetDays = 1 };
        var threeDaysAgo = new ReportTaskLine { Date = Today.AddDays(-3), DueOffsetDays = 3 };
        var tomorrow = new ReportTaskLine { Date = Today.AddDays(1), DueOffsetDays = -1 };

        Assert.Equal("今天到期", today.DelayText);
        // 措辞由 "已拖 N 天" 统一成 "已逾期 N 天"：任务行的小徽标、悬浮提示、报告三处
        // 现在读的是同一份定义（TimeText.DescribeDueOffset），"拖"是口语、"逾期"是和提醒
        // 与设置界面一致的正式说法，混用会让人以为是两个不同的指标。
        Assert.Equal("已逾期 1 天", yesterday.DelayText);
        Assert.Equal("已逾期 3 天", threeDaysAgo.DelayText);
        Assert.Equal("还有 1 天到期", tomorrow.DelayText);

        // 口径必须与提醒那套（GetOverdueDays）同源，否则两个功能会各说各话。
        Assert.Equal(
            new CalendarTask { Date = yesterday.Date }.GetOverdueDays(Today),
            yesterday.DueOffsetDays);
    }

    [Fact]
    public void StillOpen_IsSortedByDueDate_MostUrgentFirst()
    {
        var report = ScreenshotReport(
            Open("早就该做完的", new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 19)));

        // 按**到期日**升序：拖最久的排最前，远期任务沉底。
        // 旧实现按"创建天数"排，会把 10/24 的任务排到 09/15 的前面。
        var dates = report.StillOpen.Select(line => line.Date).ToList();
        Assert.Equal(dates.OrderBy(d => d), dates);
        Assert.Equal("早就该做完的", report.StillOpen[0].Title);
        Assert.Equal("已逾期 5 天", report.StillOpen[0].DelayText);
    }

    [Fact]
    public void PlainTextRenderer_UsesTheSameDelayWording()
    {
        // 纯文本是第三种呈现（自定义 webhook / 预览），口径也必须只有一份。
        var text = TaskReportBuilder.RenderText(ScreenshotReport());

        Assert.DoesNotContain("已拖", text);
        Assert.DoesNotContain("当天新建", text);
        Assert.Contains("还有 1 天到期", text);
    }

    // ===== 二、方言：飞书卡片不许出现块级记号 =====

    [Fact]
    public void FeishuMarkdown_ContainsNoBlockLevelSyntax()
    {
        var markdown = TaskReportBuilder.RenderFeishuMarkdown(ScreenshotReport());

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimStart();
            Assert.False(line.StartsWith('#'), $"lark_md 不认 # 标题，会原样显示：{raw}");
            Assert.False(line.StartsWith('>'), $"lark_md 不认 > 引用，会原样显示：{raw}");
            Assert.False(line.StartsWith("- "), $"lark_md 不认 - 列表，会原样显示：{raw}");
            Assert.False(line.StartsWith("---", StringComparison.Ordinal), $"lark_md 不认分隔线：{raw}");
        }

        // 它认的那部分要保留：加粗 + 项目符号字符。
        Assert.Contains("**桌面日历 · 周报**", markdown);
        Assert.Contains("• ", markdown);
        Assert.Contains("还有 1 天到期", markdown);
    }

    [Fact]
    public void WeComMarkdown_KeepsBlockLevelSyntax()
    {
        var markdown = TaskReportBuilder.RenderMarkdown(ScreenshotReport());

        Assert.Contains("# 桌面日历 · 周报", markdown);
        Assert.Contains("## 📊 总览", markdown);
        Assert.Contains("> 期间到期", markdown);
        Assert.Contains("- ", markdown);
    }

    [Fact]
    public void BothMarkdownDialects_CarryTheSameContent()
    {
        // 内容不可能漂移：两条渠道的正文只来自同一个 BuildBody，差异只允许在语法翻译那一层。
        var report = ScreenshotReport(
            Open("早就该做完的", new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 19)));
        var wecom = TaskReportBuilder.RenderMarkdown(report);
        var feishu = TaskReportBuilder.RenderFeishuMarkdown(report);

        foreach (var line in report.StillOpen)
        {
            Assert.Contains(line.Title, wecom);
            Assert.Contains(line.Title, feishu);
        }

        // 每个存在的分组标题两边都要有 —— 这正是"同一份字符串喂两个渠道"时最容易漏的地方。
        Assert.Contains("📊 总览", wecom);
        Assert.Contains("📊 总览", feishu);
        Assert.Contains("📌 至今未完成", wecom);
        Assert.Contains("📌 至今未完成", feishu);
        Assert.Contains("🚨 逾期未完成", wecom);
        Assert.Contains("🚨 逾期未完成", feishu);
    }

    // ===== 三、用户可控标题：两种方言都不许露出记号 =====

    [Fact]
    public void MarkdownInTaskTitle_NeverLeaksInEitherDialect()
    {
        var report = ScreenshotReport(
            Open("**考前**练车 #1", new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 18)));

        var wecom = TaskReportBuilder.RenderMarkdown(report);
        var feishu = TaskReportBuilder.RenderFeishuMarkdown(report);

        // 飞书侧直接剥掉记号（不赌 lark_md 认不认反斜杠转义）。
        Assert.Contains("考前练车 #1", feishu);
        Assert.DoesNotContain("**考前**", feishu);

        // 企微侧保留原有转义策略，但同样不能把 ** 原样送出去。
        Assert.DoesNotContain("**考前**", wecom);
    }

    [Fact]
    public void EmptyTitle_FallsBackToPlaceholder()
    {
        var report = ScreenshotReport(Open("   ", new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 18)));

        Assert.Contains("(无标题)", TaskReportBuilder.RenderFeishuMarkdown(report));
        Assert.Contains("(无标题)", TaskReportBuilder.RenderMarkdown(report));
    }
}
