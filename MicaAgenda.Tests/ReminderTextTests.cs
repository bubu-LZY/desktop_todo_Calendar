using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Tests;

/// <summary>
/// 提醒推送的文案。
///
/// <para>这一组守的是用户明确提的两条要求：① 推送内容要「简洁明了、内容完整」；
/// ② <b>不要把源码样式发出去</b> —— 推送走的是纯文本消息，而任务标题支持 Markdown，
/// 直接插进去会把 <c>**粗体**</c>、<c># 标题</c>、换行这些记号原样露给用户。</para>
///
/// <para>另外锁住逾期口径：<b>跨过任务当日的 24:00 才算逾期</b>，
/// 所以"今天"的任务即便时刻已过也仍然只是当天待办。</para>
/// </summary>
public sealed class ReminderTextTests
{
    private static readonly DateOnly Today = new(2026, 9, 20);

    private static CalendarTask Task(
        string title,
        DateOnly? date = null,
        bool completed = false,
        bool important = false,
        TimeOnly? time = null) => new()
        {
            Id = Guid.NewGuid(),
            Date = date ?? Today,
            Title = title,
            IsCompleted = completed,
            IsImportant = important,
            Time = time,
            CreatedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero)
        };

    // ===== 逾期口径 =====

    [Fact]
    public void IsOverdue_TodaysUnfinishedTask_IsNotOverdue()
    {
        // 用户指定的口径：跨过任务当日的 24:00 才算逾期 —— 所以今天的一律还不算逾期，
        // 哪怕它的时刻（09:00）早就过了。
        var task = Task("今天的事", time: new TimeOnly(9, 0));

        Assert.False(ReminderTextBuilder.IsOverdue(task, Today));
    }

    [Fact]
    public void IsOverdue_YesterdaysUnfinishedTask_IsOverdue()
    {
        Assert.True(ReminderTextBuilder.IsOverdue(Task("昨天的事", Today.AddDays(-1)), Today));
    }

    [Fact]
    public void IsOverdue_CompletedTaskIsNeverOverdue()
    {
        Assert.False(ReminderTextBuilder.IsOverdue(Task("已做完", Today.AddDays(-5), completed: true), Today));
    }

    [Fact]
    public void IsWithinOverdueWindow_CoversLookbackAndExcludesOlder()
    {
        var edge = Today.AddDays(-ReminderTextBuilder.OverdueLookbackDays);

        Assert.True(ReminderTextBuilder.IsWithinOverdueWindow(Task("刚好在窗口内", edge), Today));
        Assert.False(ReminderTextBuilder.IsWithinOverdueWindow(Task("太老了", edge.AddDays(-1)), Today));
    }

    // ===== 每日汇总 =====

    [Fact]
    public void DailyDigest_ShowsPendingAndCompleted_AndStatesTheOverdueBoundary()
    {
        var tasks = new List<CalendarTask>
        {
            Task("买菜"),
            Task("写周报", important: true),
            Task("晨跑", completed: true)
        };

        var text = ReminderTextBuilder.BuildDailyDigest(Today, tasks, []);

        Assert.Contains("【桌面日历提醒】2026年9月20日", text);
        // 边界必须写在标题行里，用户才知道这些未完成项什么时候会变成逾期
        Assert.Contains("过今天 24:00 未完成即算逾期", text);
        Assert.Contains("今日任务共 3 项，待完成 2 项", text);
        Assert.Contains("1. ⬜ 写周报 ⭐", text);
        Assert.Contains("买菜", text);
        Assert.Contains("✅ 晨跑", text);
        // 没有逾期时不该出现逾期段
        Assert.DoesNotContain("已逾期", text);
    }

    [Fact]
    public void DailyDigest_ListsOverdueWithTheirOwnDates()
    {
        var overdue = new List<CalendarTask>
        {
            Task("提交报销", Today.AddDays(-2)),
            Task("整理数据", Today.AddDays(-1))
        };

        var text = ReminderTextBuilder.BuildDailyDigest(Today, [Task("买菜")], overdue);

        Assert.Contains("已逾期 2 项", text);
        Assert.Contains($"{ReminderTextBuilder.OverdueLookbackDays} 天", text);
        // 逾期项必须带自己的日期，否则用户不知道是哪天欠的
        Assert.Contains("9月18日 提交报销", text);
        Assert.Contains("9月19日 整理数据", text);
    }

    [Fact]
    public void DailyDigest_StillReportsOverdue_WhenTodayHasNoTasks()
    {
        // 旧版在「当天无任务」时直接 return + 记标记，于是逾期欠账永远不会被推出来。
        var text = ReminderTextBuilder.BuildDailyDigest(Today, [], [Task("提交报销", Today.AddDays(-3))]);

        Assert.Contains("今日没有任务安排。", text);
        Assert.Contains("已逾期 1 项", text);
        Assert.Contains("提交报销", text);
    }

    [Fact]
    public void DailyDigest_SaysAllDone_WhenEverythingIsCompleted()
    {
        var text = ReminderTextBuilder.BuildDailyDigest(Today, [Task("早餐", completed: true)], []);

        Assert.Contains("已全部完成", text);
        Assert.DoesNotContain("待完成", text);
    }

    [Fact]
    public void DailyDigest_StripsMarkdownFromTitles()
    {
        // 用户要求：推送里不能出现 Markdown 源码样式。
        var tasks = new List<CalendarTask> { Task("**写周报**"), Task("# 今天要开会") };

        var text = ReminderTextBuilder.BuildDailyDigest(Today, tasks, []);

        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("#", text);
        Assert.Contains("写周报", text);
        Assert.Contains("今天要开会", text);
    }

    [Fact]
    public void DailyDigest_KeepsEachTaskOnOneLine()
    {
        // 标题里带换行 / 列表记号会把「1. xxx」的编号结构撑坏。
        var tasks = new List<CalendarTask> { Task("第一行\n第二行"), Task("- 列表式标题") };

        var text = ReminderTextBuilder.BuildDailyDigest(Today, tasks, []);

        Assert.Contains("第一行 第二行", text);
        Assert.Contains("• 列表式标题", text);
        // 每条任务只占一行：2 条任务 → 编号行正好 2 行
        var numbered = text.Split('\n').Count(line => line.StartsWith('1') || line.StartsWith('2'));
        Assert.Equal(2, numbered);
    }

    [Fact]
    public void DailyDigest_EmptyTitleGetsAPlaceholder()
    {
        var text = ReminderTextBuilder.BuildDailyDigest(Today, [Task("   ")], []);

        Assert.Contains("(无标题)", text);
    }

    // ===== 逾期预警 =====

    [Fact]
    public void OverdueWarning_ListsTodaysRemaining_AndStatesTheDeadline()
    {
        var pending = new List<CalendarTask>
        {
            Task("写周报", important: true, time: new TimeOnly(14, 0)),
            Task("买菜")
        };

        var text = ReminderTextBuilder.BuildOverdueWarning(Today, pending, earlierOverdueCount: 0);

        Assert.Contains("【桌面日历逾期预警】2026年9月20日", text);
        Assert.Contains("今天还有 2 项没完成，过了 24:00 就算逾期", text);
        Assert.Contains("1. ⬜ 写周报 ⭐（14:00）", text);
        Assert.Contains("2. ⬜ 买菜", text);
        // 没有往期欠账时不写这一行，避免噪声
        Assert.DoesNotContain("往期逾期", text);
    }

    [Fact]
    public void OverdueWarning_MentionsEarlierOverdueCountOnly()
    {
        // 往期已逾期的只报总数，不重复列明细（每天列同一批旧账太吵）。
        var text = ReminderTextBuilder.BuildOverdueWarning(Today, [Task("买菜")], earlierOverdueCount: 4);

        Assert.Contains("另有 4 项往期逾期未完成。", text);
        Assert.DoesNotContain("已逾期 4 项", text);
    }

    [Fact]
    public void OverdueWarning_StripsMarkdownFromTitles()
    {
        var text = ReminderTextBuilder.BuildOverdueWarning(Today, [Task("**写周报**")], 0);

        Assert.DoesNotContain("**", text);
        Assert.Contains("写周报", text);
    }

    // ===== 到点提醒 =====

    [Fact]
    public void TaskReminder_StripsMarkdownFromTheTitle()
    {
        var text = ReminderTextBuilder.BuildTaskReminder(Task("**写周报**"), lead: 30);

        Assert.DoesNotContain("**", text);
        Assert.Contains("「写周报」将在", text);
    }

    // ===== Markdown → 单行 =====

    [Fact]
    public void ToSingleLine_CollapsesNewlinesAndBlankRuns()
    {
        Assert.Equal("第一行 第二行", MarkdownText.ToSingleLine("第一行\n第二行"));
        Assert.Equal("A B", MarkdownText.ToSingleLine("A\n\n\n  B"));
        Assert.Equal(string.Empty, MarkdownText.ToSingleLine("   \n  "));
        Assert.Equal(string.Empty, MarkdownText.ToSingleLine(null));
    }

    [Fact]
    public void ToSingleLine_RemovesEmphasisButKeepsArithmetic()
    {
        Assert.Equal("重点", MarkdownText.ToSingleLine("**重点**"));
        Assert.Equal("算 3*4*5 的结果", MarkdownText.ToSingleLine("算 3*4*5 的结果"));
    }
}
