using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;
using MicaAgenda.App.ViewModels;

namespace MicaAgenda.Tests;

/// <summary>
/// v4.0.0 新增的「任务具体时间 + 到点提醒 + 任务条序号」相关行为。
/// </summary>
public sealed class TaskScheduleTests
{
    private static IEnumerable<DayCellViewModel> MonthDays(MainViewModel viewModel)
        => viewModel.TimelineMonths.SelectMany(block => block.Days);

    [Fact]
    public void AddTask_WithLead_AnchorsToTheDefaultNineAm()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "组会汇报", 60);

        // 没选时间：Time 留空，锚点走当天 9:00
        Assert.Null(task.Time);
        Assert.Equal(60, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 8, 0, 0), task.ReminderTriggerAt());
    }

    [Fact]
    public void AddTask_WithoutLead_NeverReminds()
    {
        var viewModel = new MainViewModel(new CalendarData());

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "随手记");

        Assert.Null(task.Time);
        Assert.Null(task.ReminderLeadMinutes);
        Assert.Null(task.ReminderTriggerAt());
        Assert.False(task.ShouldFireReminder(new DateTime(2026, 5, 12, 12, 0, 0)));
        Assert.False(task.IsReminderExpired(new DateTime(2026, 5, 20, 12, 0, 0)));
    }

    [Fact]
    public void AddTask_NullOrNegativeLead_IsTreatedAsNoReminder()
    {
        var viewModel = new MainViewModel(new CalendarData());

        foreach (var lead in new int?[] { null, -30 })
        {
            var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "无提醒", lead);
            Assert.Null(task.ReminderLeadMinutes);
            Assert.Null(task.ReminderTriggerAt());
        }
    }

    /// <summary>
    /// v4.5.0 新增的「到时提醒」：提前量 0，任务时刻那一刻推，和「不提醒」（null）必须分开。
    /// 以前 0 被当成"没设提前量"存成 null，选了也永远不推。
    /// </summary>
    [Fact]
    public void AddTask_OnTimeLead_FiresAtTheTaskTime()
    {
        var viewModel = new MainViewModel(new CalendarData());

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "开会", 0, new TimeOnly(15, 0));

        Assert.Equal(0, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 15, 0, 0), task.ReminderTriggerAt());
        Assert.True(task.ShouldFireReminder(new DateTime(2026, 5, 12, 15, 0, 0)));

        // 差一分钟不算到点
        Assert.False(task.ShouldFireReminder(new DateTime(2026, 5, 12, 14, 59, 0)));
    }

    /// <summary>没选任务时刻时，锚点是当天 9:00，「到时提醒」就是 9:00 那一刻推。</summary>
    [Fact]
    public void AddTask_OnTimeLead_WithoutChosenTime_AnchorsToNineAm()
    {
        var viewModel = new MainViewModel(new CalendarData());

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "默认时刻", 0);

        Assert.Null(task.Time);
        Assert.Equal(0, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 9, 0, 0), task.ReminderTriggerAt());
    }

    [Fact]
    public void LegacyTaskWithStoredTime_StillUsesThatTimeAsAnchor()
    {
        var task = ScheduledTask(new TimeOnly(14, 30), 30);

        Assert.Equal(new DateTime(2026, 5, 10, 14, 0, 0), task.ReminderTriggerAt());
    }

    /// <summary>选了任务时刻后，提醒量从那个时刻往前推（不再是固定当天 9:00）。</summary>
    [Fact]
    public void AddTask_WithChosenTime_AnchorsReminderToThatTime()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "下午的评审", 30, new TimeOnly(15, 0));

        Assert.Equal(new TimeOnly(15, 0), task.Time);
        Assert.Equal(new DateTime(2026, 5, 12, 14, 30, 0), task.ReminderTriggerAt());
        Assert.Equal(new DateTime(2026, 5, 12, 15, 0, 0), task.ScheduledAt);
    }

    [Fact]
    public void CommitTodayTask_CarriesLeadFromTheForm()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.BeginAddTodayTask();
        viewModel.TodayTaskDraft = "写周报";
        viewModel.TodayTaskLead = "提前1个小时";
        viewModel.TodayTaskTime = new TimeSpan(14, 0, 0);

        var task = viewModel.CommitTodayTask();

        Assert.NotNull(task);
        Assert.Equal(60, task!.ReminderLeadMinutes);
        Assert.Equal(new TimeOnly(14, 0), task.Time);
        Assert.Equal(new DateTime(2026, 5, 10, 13, 0, 0), task.ReminderTriggerAt());
        Assert.Equal(new DateOnly(2026, 5, 10), task.Date);

        // 提交后表单清空，下次添加不会带着上一条的提前量和时刻
        Assert.False(viewModel.IsAddingTodayTask);
        Assert.Null(viewModel.TodayTaskReminderLead);
        Assert.Equal("不提醒", viewModel.TodayTaskLead);
        Assert.Equal(CalendarTask.DefaultTime, viewModel.TodayTaskTimeOnly);
    }

    [Theory]
    [InlineData("不提醒", null)]
    [InlineData("到时提醒", 0)]
    [InlineData("提前3分钟", 3)]
    [InlineData("提前5分钟", 5)]
    [InlineData("提前10分钟", 10)]
    [InlineData("提前15分钟", 15)]
    [InlineData("提前30分钟", 30)]
    [InlineData("提前1个小时", 60)]
    [InlineData("提前3个小时", 180)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("随便什么", null)]
    public void ReminderLeadCatalog_MapsEveryOptionToMinutes(string? label, int? expected)
        => Assert.Equal(expected, ReminderLeadCatalog.ToMinutes(label));

    /// <summary>反查：存下来的提前量要能还原成下拉标签，编辑任务时下拉才不会空着。</summary>
    [Theory]
    [InlineData(null, "不提醒")]
    [InlineData(0, "到时提醒")]
    [InlineData(-5, "到时提醒")]
    [InlineData(3, "提前3分钟")]
    [InlineData(180, "提前3个小时")]
    // 不在档位表里的自定义值（MCP / HTTP 写进来的）取不超过它的最大档位
    [InlineData(120, "提前1个小时")]
    [InlineData(5, "提前5分钟")]
    public void ReminderLeadCatalog_MapsMinutesBackToALabel(int? minutes, string expected)
        => Assert.Equal(expected, ReminderLeadCatalog.ToLabel(minutes));

    /// <summary>反查结果必须永远落在下拉表里，否则编辑框会显示一个不存在的选项。</summary>
    [Fact]
    public void ReminderLeadCatalog_ReverseLookupAlwaysLandsOnADropdownOption()
    {
        foreach (var minutes in new int?[] { null, -100, -1, 0, 1, 7, 29, 45, 59, 61, 179, 181, 1440 })
        {
            Assert.Contains(ReminderLeadCatalog.ToLabel(minutes), ReminderLeadCatalog.Labels);
        }
    }

    [Fact]
    public void ReminderLeadDropdown_ExposesExactlyTheFixedOptions()
    {
        var viewModel = new MainViewModel(new CalendarData());

        Assert.Equal(
            ["不提醒", "到时提醒", "提前3分钟", "提前5分钟", "提前10分钟", "提前15分钟", "提前30分钟", "提前1个小时", "提前3个小时"],
            viewModel.ReminderLeadOptions.ToArray());

        viewModel.TodayTaskLead = "提前15分钟";
        Assert.Equal(15, viewModel.TodayTaskReminderLead);

        viewModel.TodayTaskLead = "到时提醒";
        Assert.Equal(0, viewModel.TodayTaskReminderLead);

        viewModel.TodayTaskLead = "不提醒";
        Assert.Null(viewModel.TodayTaskReminderLead);
    }

    /// <summary>日期格子里的快速添加表单与右侧面板共用同一份提醒档位，且开始/取消都要复位。</summary>
    [Fact]
    public void DayCellQuickAdd_StartsFromNoReminderAndResetsOnCancel()
    {
        var cell = new DayCellViewModel(new DateOnly(2026, 9, 15), isInCurrentMonth: true, isToday: true, [], []);

        Assert.Equal(ReminderLeadCatalog.Labels, cell.ReminderLeadOptions);
        Assert.Equal("不提醒", cell.ReminderLead);
        Assert.Null(cell.DraftReminderLead);

        cell.ReminderLead = "提前30分钟";
        Assert.Equal(30, cell.DraftReminderLead);

        // 取消后要回到「不提醒」，否则下一次新建会莫名其妙带上上一次的提醒。
        cell.CancelAdd();
        Assert.Equal("不提醒", cell.ReminderLead);
        Assert.Null(cell.DraftReminderLead);

        cell.ReminderLead = "提前3个小时";
        cell.BeginAdd();
        Assert.Equal("不提醒", cell.ReminderLead);
        Assert.Equal(string.Empty, cell.DraftTitle);
        Assert.Null(cell.DraftReminderLead);
    }

    /// <summary>
    /// 日期格子里的快速添加也要能选「任务时刻」，默认当天 9:00，
    /// 并且开始 / 取消都要把它复位（否则下一条任务会莫名带着上一次选的时间）。
    /// </summary>
    [Fact]
    public void DayCellQuickAdd_CarriesDraftTimeAndResetsIt()
    {
        var cell = new DayCellViewModel(new DateOnly(2026, 9, 15), isInCurrentMonth: true, isToday: true, [], []);

        Assert.Equal(CalendarTask.DefaultTime, cell.DraftTimeOnly);

        cell.DraftTime = new TimeSpan(15, 30, 0);
        Assert.Equal(new TimeOnly(15, 30), cell.DraftTimeOnly);

        // 取消 → 回到当天 9:00
        cell.CancelAdd();
        Assert.Equal(CalendarTask.DefaultTime, cell.DraftTimeOnly);

        // 清空选择（TimePicker 允许清成 null）→ 也当 9:00
        cell.DraftTime = null;
        Assert.Equal(CalendarTask.DefaultTime, cell.DraftTimeOnly);

        cell.DraftTime = new TimeSpan(8, 5, 0);
        cell.BeginAdd();
        Assert.Equal(CalendarTask.DefaultTime, cell.DraftTimeOnly);
    }

    // ===== v4.5.0：右键「编辑」把标题 / 任务时刻 / 提醒档位一起改 =====

    /// <summary>三个字段一次改完（用户报的"编辑时改不了提醒时间和任务时间"就是这个方法要覆盖的）。</summary>
    [Fact]
    public void UpdateTask_ChangesTitleTimeAndReminderLead()
    {
        var viewModel = NewViewModelWithClock();
        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "旧标题");

        Assert.True(viewModel.UpdateTask(task.Id, "新标题", new TimeOnly(15, 30), 10));

        Assert.Equal("新标题", task.Title);
        Assert.Equal(new TimeOnly(15, 30), task.Time);
        Assert.Equal(10, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 15, 20, 0), task.ReminderTriggerAt());
    }

    /// <summary>改成「到时提醒」：提前量 0 要如实存下来，不能又被当成「不提醒」。</summary>
    [Fact]
    public void UpdateTask_ToOnTimeReminder_StoresZeroInsteadOfNull()
    {
        var viewModel = NewViewModelWithClock();
        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "开会", 30, new TimeOnly(15, 0));

        viewModel.UpdateTask(task.Id, task.Title, new TimeOnly(15, 0), 0);

        Assert.Equal(0, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 15, 0, 0), task.ReminderTriggerAt());
    }

    /// <summary>
    /// 时刻 / 档位变了要作废原来的一次性提醒标记：否则「把 9 点改成 15 点」之后，
    /// 当天再也不会响（旧标记把新时刻挡住了）。
    /// </summary>
    [Fact]
    public void UpdateTask_ResetsReminderMark_WhenScheduleChanged()
    {
        var viewModel = NewViewModelWithClock();
        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "开会", 30, new TimeOnly(10, 0));
        task.MarkReminderSent(new DateTimeOffset(2026, 5, 12, 9, 30, 0, TimeSpan.Zero));

        viewModel.UpdateTask(task.Id, task.Title, new TimeOnly(15, 0), 30);

        Assert.Null(task.ReminderSentAt);
        Assert.True(task.ShouldFireReminder(new DateTime(2026, 5, 12, 14, 30, 0)));
    }

    /// <summary>只改标题不动提醒：不要把已经推过的提醒又推一遍。</summary>
    [Fact]
    public void UpdateTask_KeepsReminderMark_WhenOnlyTitleChanged()
    {
        var viewModel = NewViewModelWithClock();
        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "开会", 30, new TimeOnly(10, 0));
        task.MarkReminderSent(new DateTimeOffset(2026, 5, 12, 9, 30, 0, TimeSpan.Zero));

        viewModel.UpdateTask(task.Id, "换个名字", new TimeOnly(10, 0), 30);

        Assert.Equal("换个名字", task.Title);
        Assert.NotNull(task.ReminderSentAt);
        Assert.False(task.ShouldFireReminder(new DateTime(2026, 5, 12, 10, 0, 0)));
    }

    /// <summary>编辑框被清空时不要把任务名抹掉；清掉时间则回到当天 9:00（存 null）。</summary>
    [Fact]
    public void UpdateTask_BlankTitleKeepsOldOne_AndNineAmStoresNull()
    {
        var viewModel = NewViewModelWithClock();
        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "原标题", 30, new TimeOnly(15, 0));

        viewModel.UpdateTask(task.Id, "   ", new TimeOnly(9, 0), null);

        Assert.Equal("原标题", task.Title);
        Assert.Null(task.Time);
        Assert.Null(task.ReminderLeadMinutes);
        Assert.Null(task.ReminderTriggerAt());
    }

    /// <summary>负的提前量没有意义，落到「不提醒」。</summary>
    [Fact]
    public void UpdateTask_NegativeLead_FallsBackToNoReminder()
    {
        var viewModel = NewViewModelWithClock();
        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "任务", 30, new TimeOnly(15, 0));

        viewModel.UpdateTask(task.Id, "任务", new TimeOnly(15, 0), -30);

        Assert.Null(task.ReminderLeadMinutes);
        Assert.Null(task.ReminderTriggerAt());
    }

    /// <summary>Id 找不到（刚被删掉）时安静返回 false，不抛异常。</summary>
    [Fact]
    public void UpdateTask_UnknownId_ReturnsFalse()
    {
        var viewModel = NewViewModelWithClock();

        Assert.False(viewModel.UpdateTask(Guid.NewGuid(), "标题", new TimeOnly(15, 0), 30));
    }

    private static MainViewModel NewViewModelWithClock()
        => new(new CalendarData(), () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

    private static CalendarTask ScheduledTask(TimeOnly? time, int? lead)
        => new()
        {
            Id = Guid.NewGuid(),
            Date = new DateOnly(2026, 5, 10),
            Title = "组会",
            CreatedAt = new DateTimeOffset(2026, 5, 9, 8, 0, 0, TimeSpan.Zero),
            Time = time,
            ReminderLeadMinutes = lead
        };

    [Fact]
    public void ShouldFireReminder_OnlyInsideTheTriggerWindow()
    {
        var task = ScheduledTask(new TimeOnly(10, 0), 30);

        Assert.False(task.ShouldFireReminder(new DateTime(2026, 5, 10, 9, 29, 59)));
        Assert.True(task.ShouldFireReminder(new DateTime(2026, 5, 10, 9, 30, 0)));
        Assert.True(task.ShouldFireReminder(new DateTime(2026, 5, 10, 10, 0, 0)));

        // 当天窗口末尾仍算数，过了当天就只记「已推」、不补发
        Assert.True(task.ShouldFireReminder(new DateTime(2026, 5, 10, 23, 59, 59)));
        Assert.False(task.ShouldFireReminder(new DateTime(2026, 5, 11, 0, 0, 1)));
        Assert.True(task.IsReminderExpired(new DateTime(2026, 5, 11, 0, 0, 1)));
        Assert.False(task.IsReminderExpired(new DateTime(2026, 5, 10, 12, 0, 0)));
    }

    [Fact]
    public void ShouldFireReminder_SkipsCompletedAndAlreadySent()
    {
        var completed = ScheduledTask(new TimeOnly(10, 0), 30);
        completed.MarkCompleted(new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        Assert.False(completed.ShouldFireReminder(new DateTime(2026, 5, 10, 10, 0, 0)));

        var sent = ScheduledTask(new TimeOnly(10, 0), 30);
        sent.MarkReminderSent(new DateTimeOffset(2026, 5, 10, 9, 30, 0, TimeSpan.Zero));
        Assert.False(sent.ShouldFireReminder(new DateTime(2026, 5, 10, 9, 31, 0)));
        Assert.False(sent.IsReminderExpired(new DateTime(2026, 5, 11, 9, 0, 0)));

        // 改了提前量之后要能再推一次
        sent.ResetReminder();
        Assert.True(sent.ShouldFireReminder(new DateTime(2026, 5, 10, 9, 31, 0)));
    }

    [Fact]
    public void Normalize_ClearsReminderMarkWhenLeadIsRemoved()
    {
        var task = ScheduledTask(new TimeOnly(10, 0), 30);
        task.MarkReminderSent(new DateTimeOffset(2026, 5, 10, 9, 30, 0, TimeSpan.Zero));

        task.ReminderLeadMinutes = null;
        task.Normalize(DateTimeOffset.Now);

        Assert.Null(task.ReminderSentAt);
    }

    [Fact]
    public void Normalize_ClampsNegativeLeadToZero()
    {
        var task = ScheduledTask(new TimeOnly(10, 0), -30);

        task.Normalize(DateTimeOffset.Now);

        Assert.Equal(0, task.ReminderLeadMinutes);
    }

    [Fact]
    public void DayCell_AssignsSequentialOrderNumbers()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        var day = new DateOnly(2026, 5, 12);

        viewModel.AddTask(day, "第一件");
        viewModel.AddTask(day, "第二件");
        viewModel.AddTask(day, "第三件");

        var cell = MonthDays(viewModel).Single(c => c.Date == day);
        Assert.Equal(["1.", "2.", "3."], cell.Tasks.Select(task => task.OrderText).ToArray());
        Assert.Equal([1, 2, 3], cell.Tasks.Select(task => task.OrderIndex).ToArray());
    }

    [Fact]
    public void TaskItemViewModel_ExposesReminderInsteadOfTime()
    {
        var withLead = new TaskItemViewModel(ScheduledTask(new TimeOnly(14, 5), 30));

        Assert.True(withLead.HasReminder);
        Assert.Equal("13:35", withLead.ReminderTimeText);
        Assert.StartsWith("13:35 提醒 · ", withLead.TimeBadge);
        Assert.Contains("提醒：13:35", withLead.TooltipText);
        Assert.Contains("提前 30分钟", withLead.TooltipText);

        var withoutLead = new TaskItemViewModel(ScheduledTask(null, null));
        Assert.False(withoutLead.HasReminder);
        Assert.Equal(string.Empty, withoutLead.ReminderTimeText);
        Assert.DoesNotContain("提醒 ·", withoutLead.TimeBadge);
    }

    [Fact]
    public async Task CalendarDataStore_GivesDuplicateIdsAFreshOne()
    {
        // 整个 UI 都按 Id 复用 ViewModel 实例；两条任务共用 Id 时，
        // 日期格子里会比右侧面板少显示一条。加载时一次性改正。
        var shared = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-dup-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "Settings": { "ViewMode": "Month", "BackgroundMode": "Graphite", "Opacity": 1 },
              "Tasks": [
                { "Id": "{{shared}}", "Date": "2026-05-12", "Title": "第一条", "CreatedAt": "2026-05-01T09:00:00+08:00" },
                { "Id": "{{shared}}", "Date": "2026-05-12", "Title": "第二条", "CreatedAt": "2026-05-01T09:05:00+08:00" },
                { "Id": "00000000-0000-0000-0000-000000000000", "Date": "2026-05-12", "Title": "空 Id", "CreatedAt": "2026-05-01T09:10:00+08:00" }
              ]
            }
            """);

        var loaded = await new CalendarDataStore(path).LoadAsync();

        Assert.Equal(3, loaded.Tasks.Count);
        Assert.Equal(3, loaded.Tasks.Select(task => task.Id).Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, loaded.Tasks.Select(task => task.Id));
        File.Delete(path);
    }

    [Fact]
    public async Task CalendarDataStore_MigratesRemovedBackgroundThemes()
    {
        foreach (var (legacy, expected) in new[]
                 {
                     ("Glass", CalendarBackgroundMode.FrostedWhite),
                     ("Transparent", CalendarBackgroundMode.FrostedWhite),
                     ("Solid", CalendarBackgroundMode.FrostedWhite),
                     ("ClearBorder", CalendarBackgroundMode.None),
                     ("Graphite", CalendarBackgroundMode.Graphite)
                 })
        {
            var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-theme-{legacy}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, $$"""
                {
                  "Settings": { "ViewMode": "Month", "BackgroundMode": "{{legacy}}", "Opacity": 0.86 },
                  "Tasks": []
                }
                """);

            var loaded = await new CalendarDataStore(path).LoadAsync();

            Assert.Equal(expected, loaded.Settings.BackgroundMode);
            File.Delete(path);
        }
    }
}
