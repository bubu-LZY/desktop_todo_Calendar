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

        // 任务不再由用户填时间：Time 留空，基准时刻走当天 9:00
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
    public void AddTask_NonPositiveLead_IsTreatedAsNoReminder()
    {
        var viewModel = new MainViewModel(new CalendarData());

        foreach (var lead in new int?[] { null, 0, -30 })
        {
            var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "无提醒", lead);
            Assert.Null(task.ReminderLeadMinutes);
            Assert.Null(task.ReminderTriggerAt());
        }
    }

    [Fact]
    public void LegacyTaskWithStoredTime_StillUsesThatTimeAsAnchor()
    {
        var task = ScheduledTask(new TimeOnly(14, 30), 30);

        Assert.Equal(new DateTime(2026, 5, 10, 14, 0, 0), task.ReminderTriggerAt());
    }

    [Fact]
    public void CommitTodayTask_CarriesLeadFromTheForm()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.BeginAddTodayTask();
        viewModel.TodayTaskDraft = "写周报";
        viewModel.TodayTaskLead = "提前1个小时";

        var task = viewModel.CommitTodayTask();

        Assert.NotNull(task);
        Assert.Equal(60, task!.ReminderLeadMinutes);
        Assert.Null(task.Time);
        Assert.Equal(new DateOnly(2026, 5, 10), task.Date);

        // 提交后表单清空，下次添加不会带着上一条的提前量
        Assert.False(viewModel.IsAddingTodayTask);
        Assert.Null(viewModel.TodayTaskReminderLead);
        Assert.Equal("不提醒", viewModel.TodayTaskLead);
    }

    [Theory]
    [InlineData("不提醒", null)]
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

    [Fact]
    public void ReminderLeadDropdown_ExposesExactlyTheFixedOptions()
    {
        var viewModel = new MainViewModel(new CalendarData());

        Assert.Equal(
            ["不提醒", "提前3分钟", "提前5分钟", "提前10分钟", "提前15分钟", "提前30分钟", "提前1个小时", "提前3个小时"],
            viewModel.ReminderLeadOptions.ToArray());

        viewModel.TodayTaskLead = "提前15分钟";
        Assert.Equal(15, viewModel.TodayTaskReminderLead);

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
