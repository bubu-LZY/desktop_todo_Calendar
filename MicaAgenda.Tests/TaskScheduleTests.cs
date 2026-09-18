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
    public void AddTask_WithoutLead_DefaultsToFifteenMinutes()
    {
        var viewModel = new MainViewModel(new CalendarData());

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "随手记");

        // 新口径：不指定提醒 = 默认「提前 15 分钟」
        Assert.Null(task.Time);
        Assert.Equal(15, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 8, 45, 0), task.ReminderTriggerAt());
    }

    [Fact]
    public void AddTask_NullLead_DefaultsToFifteenMinutes()
    {
        var viewModel = new MainViewModel(new CalendarData());

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "无提醒", null);
        Assert.Equal(15, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 8, 45, 0), task.ReminderTriggerAt());
    }

    [Fact]
    public void AddTask_EmptyLeadList_IsExplicitNoReminder()
    {
        var viewModel = new MainViewModel(new CalendarData());

        // 空列表 = 明确「不提醒」，与 null（默认 15 分钟）严格区分
        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "明确不提醒", Array.Empty<int>(), null);
        Assert.Null(task.ReminderLeadMinutes);
        Assert.False(task.HasReminders);
    }

    /// <summary>
    /// 多选提醒改造后的口径：负的提前量是非法输入，统一钳到 0（「到时提醒」），
    /// 不再是「不提醒」—— 「不提醒」只能用 null / 空档位集合表达。
    /// </summary>
    [Fact]
    public void AddTask_NegativeLead_ClampsToOnTimeLead()
    {
        var viewModel = NewViewModelWithClock();

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "任务", -30, new TimeOnly(15, 0));

        Assert.Equal(0, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 15, 0, 0), task.ReminderTriggerAt());
    }

    /// <summary>
    /// v4.5.0 新增的「到时提醒」：提前量 0，任务时刻那一刻推，和「不提醒」（null）必须分开。
    /// 以前 0 被当成"没设提前量"存成 null，选了也永远不推。
    /// </summary>
    [Fact]
    public void AddTask_OnTimeLead_FiresAtTheTaskTime()
    {
        // 固定时钟在任务时刻之前：否则新建时 SuppressMissedLeadReminders 会把这个
        // 「已过点」的档位直接记账（模拟真实场景里给过去的任务补勾提醒不该立刻蹦通知）。
        var viewModel = NewViewModelWithClock();

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
        viewModel.TodayTaskLeadLabels.Add("提前1个小时");
        viewModel.TodayTaskTime = new TimeSpan(14, 0, 0);

        var task = viewModel.CommitTodayTask();

        Assert.NotNull(task);
        Assert.Equal(60, task!.ReminderLeadMinutes);
        Assert.Equal(new TimeOnly(14, 0), task.Time);
        Assert.Equal(new DateTime(2026, 5, 10, 13, 0, 0), task.ReminderTriggerAt());
        Assert.Equal(new DateOnly(2026, 5, 10), task.Date);

        // 提交后表单清空，下次添加不会带着上一条的提前量和时刻
        Assert.False(viewModel.IsAddingTodayTask);
        Assert.Empty(viewModel.TodayTaskReminderLeads);
        Assert.Equal("不提醒", viewModel.TodayTaskLeadSummary);
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

        // 多选下拉不含「不提醒」（一个都不勾就是不提醒），但含新增的「提前一天」
        Assert.Equal(
            ["到时提醒", "提前3分钟", "提前5分钟", "提前10分钟", "提前15分钟", "提前30分钟", "提前1个小时", "提前3个小时", "提前一天"],
            viewModel.ReminderLeadOptions.ToArray());

        viewModel.TodayTaskLeadLabels.Add("提前15分钟");
        Assert.Equal([15], viewModel.TodayTaskReminderLeads.ToArray());

        // 多选：再勾一档，两个提前量都保留（按下拉顺序）
        viewModel.TodayTaskLeadLabels.Add("到时提醒");
        Assert.Equal([0, 15], viewModel.TodayTaskReminderLeads.ToArray());

        viewModel.TodayTaskLeadLabels.Clear();
        Assert.Empty(viewModel.TodayTaskReminderLeads);
        Assert.Equal("不提醒", viewModel.TodayTaskLeadSummary);
    }

    /// <summary>日期格子里的快速添加表单与右侧面板共用同一份提醒档位，且开始/取消都要复位。</summary>
    [Fact]
    public void DayCellQuickAdd_StartsFromNoReminderAndResetsOnCancel()
    {
        var cell = new DayCellViewModel(new DateOnly(2026, 9, 15), isInCurrentMonth: true, isToday: true, [], []);

        // 多选档位表不含「不提醒」（空勾选 = 不提醒）
        Assert.Equal(ReminderLeadCatalog.SelectableLabels, cell.ReminderLeadOptions);
        Assert.Equal("不提醒", cell.ReminderLeadSummary);
        Assert.Empty(cell.DraftReminderLeads);

        cell.ReminderLeadLabels.Add("提前30分钟");
        Assert.Equal([30], cell.DraftReminderLeads.ToArray());

        // 取消后要回到「不提醒」，否则下一次新建会莫名其妙带上上一次的提醒。
        cell.CancelAdd();
        Assert.Equal("不提醒", cell.ReminderLeadSummary);
        Assert.Empty(cell.DraftReminderLeads);

        cell.ReminderLeadLabels.Add("提前3个小时");
        cell.BeginAdd();
        Assert.Equal("不提醒", cell.ReminderLeadSummary);
        Assert.Equal(string.Empty, cell.DraftTitle);
        Assert.Empty(cell.DraftReminderLeads);
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

        viewModel.UpdateTask(task.Id, "   ", new TimeOnly(9, 0), (int?)null);

        Assert.Equal("原标题", task.Title);
        Assert.Null(task.Time);
        Assert.Null(task.ReminderLeadMinutes);
        Assert.Null(task.ReminderTriggerAt());
    }

    /// <summary>负的提前量是非法输入：钳到 0（到时提醒），而不是悄悄变成「不提醒」。</summary>
    [Fact]
    public void UpdateTask_NegativeLead_ClampsToOnTimeLead()
    {
        var viewModel = NewViewModelWithClock();
        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "任务", 30, new TimeOnly(15, 0));

        viewModel.UpdateTask(task.Id, "任务", new TimeOnly(15, 0), -30);

        Assert.Equal(0, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 15, 0, 0), task.ReminderTriggerAt());
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
    public void TaskItemViewModel_ShowsTaskTimeAndKeepsReminderInTooltip()
    {
        var withLead = new TaskItemViewModel(ScheduledTask(new TimeOnly(14, 5), 30));

        Assert.True(withLead.HasReminder);
        // 徽标显示任务自己的时刻（14:05），不是提醒时刻（13:35）
        Assert.Equal("14:05", withLead.TaskTimeText);
        Assert.StartsWith("14:05 · ", withLead.TimeBadge);
        Assert.DoesNotContain("提醒 ·", withLead.TimeBadge);

        // 提醒档位改为只出现在悬浮提示里
        Assert.Equal("13:35", withLead.ReminderTimeText);
        Assert.Contains("提醒：13:35", withLead.TooltipText);
        Assert.Contains("提前 30分钟", withLead.TooltipText);

        var withoutLead = new TaskItemViewModel(ScheduledTask(null, null));
        Assert.False(withoutLead.HasReminder);
        Assert.Equal(string.Empty, withoutLead.ReminderTimeText);
        // 没显式设时刻的任务，徽标回落到当天默认时刻（9:00）
        Assert.Equal("09:00", withoutLead.TaskTimeText);
        Assert.StartsWith("09:00 · ", withoutLead.TimeBadge);
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

    /// <summary>勾完成后该任务必须自动沉到当天排序的最后，序号按新顺序重排（未完成在前）。</summary>
    [Fact]
    public void DayCell_CompletedTask_SinksToEndOfDayAndRenumbers()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        var day = new DateOnly(2026, 5, 12);

        var first = viewModel.AddTask(day, "第一件");
        viewModel.AddTask(day, "第二件");
        viewModel.AddTask(day, "第三件");

        viewModel.ToggleTaskCompletion(first.Id);

        var cell = MonthDays(viewModel).Single(c => c.Date == day);
        Assert.Equal(["第二件", "第三件", "第一件"], cell.Tasks.Select(t => t.Title).ToArray());
        Assert.Equal(["1.", "2.", "3."], cell.Tasks.Select(t => t.OrderText).ToArray());
        Assert.False(cell.Tasks[0].IsCompleted);
        Assert.False(cell.Tasks[1].IsCompleted);
        Assert.True(cell.Tasks[2].IsCompleted);
    }

    /// <summary>悬停「↺」还原后，任务回到未完成段，按重要度 / 创建时间的正常口径排序。</summary>
    [Fact]
    public void DayCell_RestoredTask_ReturnsToOpenSectionOrder()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        var day = new DateOnly(2026, 5, 12);

        viewModel.AddTask(day, "第一件");
        var second = viewModel.AddTask(day, "第二件");
        viewModel.ToggleTaskCompletion(second.Id);
        Assert.Equal(["第一件", "第二件"], MonthDays(viewModel).Single(c => c.Date == day).Tasks.Select(t => t.Title).ToArray());

        viewModel.ToggleTaskCompletion(second.Id);

        var titles = MonthDays(viewModel).Single(c => c.Date == day).Tasks.Select(t => t.Title).ToArray();
        Assert.Equal(["第一件", "第二件"], titles);
        Assert.All(MonthDays(viewModel).Single(c => c.Date == day).Tasks, t => Assert.False(t.IsCompleted));
    }

    /// <summary>给今天的任务勾「提前一天」：触发时刻已过且超出 60 分钟补发宽限，新建即记账，不再补陈旧提醒。</summary>
    [Fact]
    public void SuppressMissedLead_OneDayEarly_PastGrace_IsMarkedFired()
    {
        var task = new CalendarTask
        {
            Date = new DateOnly(2026, 5, 12),
            Title = "明天的事今天建",
            CreatedAt = LocalOffset(2026, 5, 12, 10, 0)
        };
        task.SetReminderLeads([1440]);

        // 触发时刻 = 5/11 09:00，宽限到 5/11 10:00；现在已是 5/12 10:00
        task.SuppressMissedLeadReminders(LocalOffset(2026, 5, 12, 10, 0));

        Assert.True(task.IsReminderFired(1440));
        Assert.Empty(task.DueReminderLeads(new DateTime(2026, 5, 12, 10, 0, 0)));
    }

    /// <summary>「提前一天」刚过点 60 分钟内开机/新建仍补发（宽限窗口内不记账）。</summary>
    [Fact]
    public void SuppressMissedLead_OneDayEarly_WithinGrace_StillArmed()
    {
        var task = new CalendarTask
        {
            Date = new DateOnly(2026, 5, 12),
            Title = "宽限内",
            CreatedAt = LocalOffset(2026, 5, 11, 9, 30)
        };
        task.SetReminderLeads([1440]);

        // 触发时刻 5/11 09:00，现在 09:30 —— 还在 60 分钟宽限内
        task.SuppressMissedLeadReminders(LocalOffset(2026, 5, 11, 9, 30));

        Assert.False(task.IsReminderFired(1440));
        Assert.Contains(1440, task.DueReminderLeads(new DateTime(2026, 5, 11, 9, 30, 0)));
    }

    /// <summary>小档位（提前 30 分钟）沿用当天 23:59:59 的补发窗口：任务日当天晚上开机仍会补发。</summary>
    [Fact]
    public void SuppressMissedLead_ShortLead_SameDayEvening_StillArmed()
    {
        var task = new CalendarTask
        {
            Date = new DateOnly(2026, 5, 12),
            Title = "当天的小档位",
            CreatedAt = LocalOffset(2026, 5, 12, 20, 0)
        };
        task.SetReminderLeads([30]);

        // 触发时刻 5/12 08:30，现在当天 20:00 —— 当天补发窗口刻意保留
        task.SuppressMissedLeadReminders(LocalOffset(2026, 5, 12, 20, 0));

        Assert.False(task.IsReminderFired(30));
        Assert.Contains(30, task.DueReminderLeads(new DateTime(2026, 5, 12, 20, 0, 0)));
    }

    /// <summary>
    /// 构造「本地墙钟」的 DateTimeOffset：任务时刻 / 补发窗口都按 LocalDateTime 比较，
    /// 写死 TimeSpan.Zero 在非 UTC 机器（如 UTC+8）上会整体平移 8 小时导致测试漂移。
    /// </summary>
    private static DateTimeOffset LocalOffset(int year, int month, int day, int hour, int minute)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }
}
