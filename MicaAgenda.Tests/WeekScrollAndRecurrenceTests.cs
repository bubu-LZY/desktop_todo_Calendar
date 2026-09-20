using MicaAgenda.App.Models;
using MicaAgenda.App.Services;
using MicaAgenda.App.ViewModels;

namespace MicaAgenda.Tests;

/// <summary>
/// v5.2.9 这一批改动的回归测试：
/// <list type="number">
///   <item>周视图「今天居中 + 可向上翻看更早日期」（<see cref="MainViewModel.ExtendWeekScrollBackward"/>）；</item>
///   <item>周期任务实例的**滚动地平线**（<see cref="RecurrenceService.TopUp"/>）——
///     修掉"最多只能加 731 个"；</item>
///   <item>周期任务在排序里权重最小、排在所有任务之后。</item>
/// </list>
/// </summary>
public sealed class WeekScrollAndRecurrenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 5, 10);

    private static MainViewModel NewViewModel(DateOnly? today = null)
    {
        var anchor = today ?? Today;
        return new MainViewModel(new CalendarData(), () =>
            new DateTimeOffset(anchor.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero));
    }

    // ===== 一、周视图：今天居中 + 向上翻看更早 =====

    [Fact]
    public void WeekScroll_StartsWithThreeDaysBeforeToday_SoTodayIsTheFourthRow()
    {
        var viewModel = NewViewModel();
        viewModel.SetViewMode(CalendarViewMode.Week);

        // 用户口径："上面 3 个格子、中间是今天、下面 3 个格子"。
        Assert.Equal(new DateOnly(2026, 5, 7), viewModel.VisibleDays[0].Date);
        Assert.Equal(3, viewModel.IndexOfTodayInWeekScroll);
        Assert.Equal(Today, viewModel.VisibleDays[3].Date);
        Assert.Equal(new DateOnly(2026, 5, 13), viewModel.VisibleDays[6].Date);
    }

    [Fact]
    public void ExtendWeekScrollBackward_PrependsEarlierDatesAndNotifies()
    {
        var viewModel = NewViewModel();
        viewModel.SetViewMode(CalendarViewMode.Week);

        var firstBefore = viewModel.VisibleDays[0].Date;
        var countBefore = viewModel.VisibleDays.Count;

        var prepended = 0;
        viewModel.WeekScrollHeadPrepended += n => prepended += n;

        var added = viewModel.ExtendWeekScrollBackward();

        // 旧实现只往尾部追加，"今天之前的日期"根本铺不出来 —— 用户想回头看前几天做不到。
        Assert.Equal(MainViewModel.WeekScrollAppendWeeks * 7, added);
        Assert.Equal(added, prepended);

        // 新插入的日期必须**紧接在原来的首日之前**，且连续到原首日的前一天。
        Assert.Equal(firstBefore.AddDays(-added), viewModel.VisibleDays[0].Date);
        for (var i = 1; i < added; i++)
        {
            Assert.Equal(viewModel.VisibleDays[i - 1].Date.AddDays(1), viewModel.VisibleDays[i].Date);
        }

        Assert.Equal(firstBefore, viewModel.VisibleDays[added].Date);

        // 今天在头部插入后索引后移 —— 宿主必须据此补偿滚动偏移，否则画面会往下跳一整屏。
        Assert.Equal(3 + added, viewModel.IndexOfTodayInWeekScroll);

        // 总长度受同一个上限约束：向上翻不会把列表撑成无限长。
        Assert.True(viewModel.VisibleDays.Count <= MainViewModel.WeekScrollMaxDays);
        Assert.Equal(countBefore + added, viewModel.VisibleDays.Count);
    }

    [Fact]
    public void ExtendWeekScrollBackward_IsNoOpOutsideWeekView()
    {
        var viewModel = NewViewModel();
        viewModel.SetViewMode(CalendarViewMode.Month);

        var count = viewModel.VisibleDays.Count;

        Assert.Equal(0, viewModel.ExtendWeekScrollBackward());
        Assert.Equal(count, viewModel.VisibleDays.Count);
    }

    // ===== 二、周期地平线：不再"最多 731 个" =====

    private static CalendarTask DailyMaster(DateOnly start, DateOnly? end = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = "每日站会",
        Date = start,
        Recurrence = RecurrenceFrequency.Daily,
        RecurrenceInterval = 1,
        RecurrenceEnd = end,
        CreatedAt = new DateTimeOffset(start.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero)
    };

    [Fact]
    public void TopUp_ExtendsAnExhaustedSeriesForwardFromToday()
    {
        // 一年前建的每日系列，早已铺完（只有一个源任务）。
        var master = DailyMaster(Today.AddDays(-365));
        var tasks = new List<CalendarTask> { master };

        var added = RecurrenceService.TopUp(tasks, Today);

        // 地平线挂在**今天**上，而不是模板日期 + 730 —— 这正是"最多只能加 731 个"的根因。
        Assert.Equal(RecurrenceService.MaxMaterializedDays + 365, added.Count);
        Assert.Equal(Today.AddDays(-364), added[0].Date);
        Assert.Equal(Today.AddDays(RecurrenceService.MaxMaterializedDays), added[^1].Date);

        // 新增的都是指向源任务的实例，且未完成（不复制完成态）。
        Assert.All(added, instance =>
        {
            Assert.Equal(master.Id, instance.SeriesId);
            Assert.False(instance.IsCompleted);
            Assert.True(instance.IsRecurring);
        });
    }

    [Fact]
    public void TopUp_IsIdempotent()
    {
        var master = DailyMaster(Today.AddDays(-30));
        var tasks = new List<CalendarTask> { master };

        var first = RecurrenceService.TopUp(tasks, Today);
        tasks.AddRange(first);

        // 把结果放回去之后再算一次：已经铺到位，不该再产出一个实例。
        // 宿主会在**每次启动**和**每次跨天**都调用它，不幂等就会天天翻倍。
        var second = RecurrenceService.TopUp(tasks, Today);
        Assert.Empty(second);
    }

    [Fact]
    public void TopUp_RespectsExplicitRecurrenceEnd()
    {
        var start = Today.AddDays(-30);
        var end = Today.AddDays(5);
        var master = DailyMaster(start, end);
        var tasks = new List<CalendarTask> { master };

        var added = RecurrenceService.TopUp(tasks, Today);

        // 用户显式设了结束日期，就不能被"地平线"顶穿：从源任务次日一直铺到结束日当天。
        // （若不尊重它，这里会一直铺到 today + MaxMaterializedDays。）
        Assert.Equal(35, added.Count);
        Assert.Equal(start.AddDays(1), added[0].Date);
        Assert.Equal(end, added[^1].Date);
        Assert.All(added, instance => Assert.True(instance.Date <= end));
    }

    [Fact]
    public void TopUp_KeepsResumingFromTheLastExistingInstance()
    {
        // 只部分铺过：源任务 + 3 个实例。续推必须从**最后一个实例**之后接，不能从源任务重算
        // （否则会产出重复日期的实例）。
        var master = DailyMaster(Today.AddDays(-10));
        var tasks = new List<CalendarTask> { master };
        for (var i = 1; i <= 3; i++)
        {
            tasks.Add(new CalendarTask
            {
                Id = Guid.NewGuid(),
                Title = master.Title,
                Date = master.Date.AddDays(i),
                SeriesId = master.Id
            });
        }

        var added = RecurrenceService.TopUp(tasks, Today);

        Assert.Equal(Today.AddDays(-6), added[0].Date);

        var allDates = tasks.Select(t => t.Date).Concat(added.Select(t => t.Date)).ToList();
        Assert.Equal(allDates.Count, allDates.Distinct().Count());
    }

    [Fact]
    public void TopUp_IgnoresPlainTasks()
    {
        var tasks = new List<CalendarTask>
        {
            new() { Id = Guid.NewGuid(), Title = "一次性任务", Date = Today }
        };

        Assert.Empty(RecurrenceService.TopUp(tasks, Today));
    }

    [Fact]
    public void ViewModelConstructor_TopsUpAndMarksDirty()
    {
        var master = DailyMaster(Today.AddDays(-400));
        var data = new CalendarData { Tasks = [master] };

        var viewModel = new MainViewModel(data, () => Now);

        Assert.True(viewModel.RecurrenceHorizonExtended);
        Assert.True(data.Tasks.Count > 1);

        // 构造期补出来的实例必须落下"脏"标记，否则要等用户下次改动才写盘。
        Assert.True(viewModel.IsDirty);
    }

    // ===== 三、周期任务排序垫底 =====

    [Fact]
    public void DayCell_PutsRecurringTasksLast()
    {
        var plainLater = new CalendarTask
        {
            Id = Guid.NewGuid(), Title = "普通任务（后建）", Date = Today,
            CreatedAt = new DateTimeOffset(2026, 5, 10, 8, 0, 0, TimeSpan.Zero)
        };
        var plainEarlier = new CalendarTask
        {
            Id = Guid.NewGuid(), Title = "普通任务（先建）", Date = Today,
            CreatedAt = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero)
        };
        var master = new CalendarTask
        {
            Id = Guid.NewGuid(), Title = "每日站会", Date = Today,
            Recurrence = RecurrenceFrequency.Daily, RecurrenceInterval = 1,
            // 故意让周期任务的创建时间**最早** —— 旧实现按创建时间排，它就会跑到最前面。
            CreatedAt = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero)
        };
        var instance = new CalendarTask
        {
            Id = Guid.NewGuid(), Title = "每周复盘", Date = Today,
            SeriesId = Guid.NewGuid(),
            CreatedAt = new DateTimeOffset(2026, 4, 2, 8, 0, 0, TimeSpan.Zero)
        };

        var data = new CalendarData { Tasks = [master, instance, plainLater, plainEarlier] };
        var viewModel = new MainViewModel(data, () => Now);

        var cell = viewModel.TimelineMonths
            .SelectMany(block => block.Days)
            .Single(day => day.Date == Today);

        var titles = cell.Tasks.Select(t => t.Title).ToList();

        // 周期任务整段垫底（源任务与实例都算），普通任务在前。
        Assert.Equal(
            ["普通任务（先建）", "普通任务（后建）", "每日站会", "每周复盘"],
            titles);
    }

    [Fact]
    public void IsRecurring_CoversBothMasterAndInstance()
    {
        var master = DailyMaster(Today);
        var instance = new CalendarTask
        {
            Id = Guid.NewGuid(), Title = "实例", Date = Today, SeriesId = master.Id
        };
        var plain = new CalendarTask { Id = Guid.NewGuid(), Title = "普通", Date = Today };

        Assert.True(master.IsRecurring);
        Assert.True(master.IsRecurringMaster);
        Assert.False(master.IsRecurringInstance);

        // 实例的 Recurrence 是 None（规则只存在源任务上），所以只判 Recurrence 会漏掉它 ——
        // UI 上就表现为"有的周期任务有【周期】标识、有的没有"。
        Assert.True(instance.IsRecurring);
        Assert.False(instance.IsRecurringMaster);
        Assert.True(instance.IsRecurringInstance);

        Assert.False(plain.IsRecurring);
    }
}
