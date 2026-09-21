using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;
using MicaAgenda.App.ViewModels;
using Xunit;

namespace MicaAgenda.Tests;

/// <summary>
/// 「还有几天 / 今天 / 已逾期几天」这套措辞的口径测试。
///
/// <para><b>起因</b>：用户看到任务行的小徽标写着"3天未完"，原话是"这个提醒好奇怪呀"。
/// 正常来说应当只有三种表达 —— 没到时间是"还有几天"、到了是"今天"、超期了是"已逾期几天"。
/// 旧实现用的是<b>从创建时间</b>算起的天数，所以一条「明天到期、三天前建」的任务
/// 会被标成"3天未完" —— 既不是还有 3 天，也不是逾期 3 天。</para>
///
/// <para>这些用例同时锁两件事：<b>口径</b>（参照物是任务自己的日期）和
/// <b>唯一实现</b>（徽标 / 悬浮提示 / 报告都从 <see cref="TimeText"/> 取，不会各自漂移）。</para>
/// </summary>
public class TaskDueTextTests
{
    // ===== 措辞本体 =====

    [Theory]
    // 未到期：还有几天
    [InlineData(-34, "还有 34 天")]
    [InlineData(-2, "还有 2 天")]
    [InlineData(-1, "还有 1 天")]
    // 今天到期
    [InlineData(0, "今天")]
    // 已逾期
    [InlineData(1, "已逾期 1 天")]
    [InlineData(3, "已逾期 3 天")]
    public void FormatDueOffset_SaysExactlyThreeThings(int offset, string expected)
        => Assert.Equal(expected, TimeText.FormatDueOffset(offset));

    [Theory]
    [InlineData(-34, "还有 34 天到期")]
    [InlineData(-1, "还有 1 天到期")]
    [InlineData(0, "今天到期")]
    [InlineData(2, "已逾期 2 天")]
    public void DescribeDueOffset_KeepsTheSameRule(int offset, string expected)
        => Assert.Equal(expected, TimeText.DescribeDueOffset(offset));

    [Fact]
    public void BothForms_ClassifyTheSameWay()
    {
        // 两种形态只是语域不同（徽标要短、提示/报告要成句），判定必须一致：
        // 同一个 offset 不能一个说"还有"、另一个说"逾期"。
        foreach (var offset in new[] { -30, -2, -1, 0, 1, 5 })
        {
            var compact = TimeText.FormatDueOffset(offset);
            var sentence = TimeText.DescribeDueOffset(offset);
            var number = Math.Abs(offset).ToString();

            if (offset > 0)
            {
                Assert.StartsWith("已逾期", compact);
                Assert.StartsWith("已逾期", sentence);
            }
            else if (offset < 0)
            {
                Assert.StartsWith("还有", compact);
                Assert.StartsWith("还有", sentence);
            }
            else
            {
                Assert.StartsWith("今天", compact);
                Assert.StartsWith("今天", sentence);
            }

            if (offset != 0)
            {
                Assert.Contains(number, compact);
                Assert.Contains(number, sentence);
            }
        }
    }

    // ===== 徽标 / 悬浮提示（用户截图里的真实数据：今天 9/21，任务 9/22）=====

    private static DateTimeOffset Now(int year, int month, int day, int hour = 22, int minute = 58)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    private static CalendarTask TaskDue(int year, int month, int day, int hour = 13)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = "正式考试",
            Date = new DateOnly(year, month, day),
            Time = new TimeOnly(hour, 0),
            // 创建得比到期日早好几天 —— 正是这个差值造成了旧的"3天未完"
            CreatedAt = Now(year, month, day).AddDays(-3)
        };

    [Fact]
    public void Badge_ShowsCountdownInsteadOfCreationAge()
    {
        // 用户截图里的那一条：今天 9/21，任务 9/22 13:00，三天前创建的。
        // 旧徽标是 "13:00 · 3天未完"，正确答案是"还有 1 天"。
        var vm = new TaskItemViewModel(TaskDue(2026, 9, 22), () => Now(2026, 9, 21));

        Assert.Equal("13:00 · 还有 1 天", vm.TimeBadge);
        Assert.DoesNotContain("未完", vm.TimeBadge);
    }

    [Fact]
    public void Badge_ShowsTodayWhenDueToday()
    {
        var vm = new TaskItemViewModel(TaskDue(2026, 9, 21), () => Now(2026, 9, 21));
        Assert.Equal("13:00 · 今天", vm.TimeBadge);
    }

    [Fact]
    public void Badge_ShowsOverdueDaysWhenPastDue()
    {
        var vm = new TaskItemViewModel(TaskDue(2026, 9, 18), () => Now(2026, 9, 21));
        Assert.Equal("13:00 · 已逾期 3 天", vm.TimeBadge);
    }

    [Fact]
    public void Badge_StillCountsAsTodayAfterTheTimeHasPassed()
    {
        // 逾期界线是**跨过任务当日的 24:00**，与提醒一致：
        // 今天 13:00 的任务，到晚上 22:58 仍是「今天」，不是「已逾期」。
        var vm = new TaskItemViewModel(TaskDue(2026, 9, 21), () => Now(2026, 9, 21, 23, 30));
        Assert.Equal("13:00 · 今天", vm.TimeBadge);
    }

    [Fact]
    public void Tooltip_ReportsDueDistanceOnceNotTwice()
    {
        // 旧提示会同时给出"未完成 3 天"和"已逾期 N 天"两个天数（口径还不同），
        // 读起来互相打架。现在只回答一个问题。
        var vm = new TaskItemViewModel(TaskDue(2026, 9, 22), () => Now(2026, 9, 21));

        Assert.Contains("还有 1 天到期", vm.TooltipText);
        Assert.DoesNotContain("未完成", vm.TooltipText);
        Assert.DoesNotContain("当天创建", vm.TooltipText);
    }

    [Fact]
    public void CompletedTask_ShowsDurationNotDueDistance()
    {
        var task = TaskDue(2026, 9, 22);
        task.MarkCompleted(Now(2026, 9, 21, 10, 0));

        var vm = new TaskItemViewModel(task, () => Now(2026, 9, 21));

        Assert.DoesNotContain("还有", vm.TimeBadge);
        Assert.DoesNotContain("逾期", vm.TimeBadge);
        Assert.StartsWith("13:00 · 用时", vm.TimeBadge);
    }

    // ===== 报告读的是同一份定义 =====

    [Fact]
    public void Report_UsesTheSameWording()
    {
        // 报告的三处渲染 + 两个宿主统计窗口都读 DelayText；
        // 这里直接盯住"它就是 TimeText 那一份"，防止以后有人在报告侧另写一套。
        foreach (var offset in new[] { -3, 0, 4 })
        {
            var line = new ReportTaskLine { Title = "x", DueOffsetDays = offset };
            Assert.Equal(TimeText.DescribeDueOffset(offset), line.DelayText);
        }
    }
}
