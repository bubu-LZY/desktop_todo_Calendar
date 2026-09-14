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
    public void AddTask_WithTimeAndLead_StoresSchedule()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "组会汇报", new TimeOnly(14, 30), 60);

        Assert.Equal(new TimeOnly(14, 30), task.Time);
        Assert.Equal(60, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 13, 30, 0), task.ReminderTriggerAt());
    }

    [Fact]
    public void AddTask_WithoutTime_DropsLeadAndNeverReminds()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data);

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "随手记", null, 120);

        Assert.Null(task.Time);
        Assert.Null(task.ReminderLeadMinutes);
        Assert.Null(task.ReminderTriggerAt());
        Assert.False(task.ShouldFireReminder(new DateTime(2026, 5, 12, 12, 0, 0)));
        Assert.False(task.IsReminderExpired(new DateTime(2026, 5, 20, 12, 0, 0)));
    }

    [Fact]
    public void AddTask_NegativeLead_IsClampedToZero()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data);

        var task = viewModel.AddTask(new DateOnly(2026, 5, 12), "到点提醒", new TimeOnly(9, 0), -30);

        Assert.Equal(0, task.ReminderLeadMinutes);
        Assert.Equal(new DateTime(2026, 5, 12, 9, 0, 0), task.ReminderTriggerAt());
    }

    [Fact]
    public void CommitTodayTask_CarriesTimeAndLeadFromTheForm()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.BeginAddTodayTask();
        viewModel.TodayTaskDraft = "写周报";
        viewModel.TodayTaskTimeText = "18:00";
        viewModel.TodayTaskLeadHour = "1时";

        var task = viewModel.CommitTodayTask();

        Assert.NotNull(task);
        Assert.Equal(new TimeOnly(18, 0), task!.Time);
        Assert.Equal(60, task.ReminderLeadMinutes);
        Assert.Equal(new DateOnly(2026, 5, 10), task.Date);

        // 提交后表单清空，下次添加不会带着上一条的时间与提前量
        Assert.False(viewModel.IsAddingTodayTask);
        Assert.Equal(string.Empty, viewModel.TodayTaskTimeText);
        Assert.Null(viewModel.TodayTaskReminderLead);
        Assert.Equal("不选", viewModel.TodayTaskLeadHour);
    }

    [Fact]
    public void ReminderLeadDropdowns_AreMutuallyExclusive()
    {
        var viewModel = new MainViewModel(new CalendarData());

        viewModel.TodayTaskLeadDay = "2天";
        Assert.Equal(2 * 24 * 60, viewModel.TodayTaskReminderLead);

        // 换选「小时」之后，天被清回「不选」，总量只按小时算
        viewModel.TodayTaskLeadHour = "3时";
        Assert.Equal("不选", viewModel.TodayTaskLeadDay);
        Assert.Equal("3时", viewModel.TodayTaskLeadHour);
        Assert.Equal(180, viewModel.TodayTaskReminderLead);

        viewModel.TodayTaskLeadMinute = "45分";
        Assert.Equal("不选", viewModel.TodayTaskLeadHour);
        Assert.Equal(45, viewModel.TodayTaskReminderLead);

        viewModel.TodayTaskLeadMinute = "不选";
        Assert.Null(viewModel.TodayTaskReminderLead);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("09:05", "09:05")]
    [InlineData("9:5", "09:05")]
    [InlineData("9：05", "09:05")]
    [InlineData("23:59", "23:59")]
    [InlineData("25:00", null)]
    [InlineData("abc", null)]
    public void ParseTimeDraft_AcceptsCommonFormats(string input, string? expected)
    {
        var parsed = MainViewModel.ParseTimeDraft(input);
        Assert.Equal(expected is null ? null : TimeOnly.Parse(expected), parsed);
    }

    private static CalendarTask ScheduledTask(TimeOnly time, int lead)
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
    public void Normalize_ClearsReminderMarkWhenTimeIsRemoved()
    {
        var task = ScheduledTask(new TimeOnly(10, 0), 30);
        task.MarkReminderSent(new DateTimeOffset(2026, 5, 10, 9, 30, 0, TimeSpan.Zero));

        task.Time = null;
        task.Normalize(DateTimeOffset.Now);

        Assert.Null(task.ReminderSentAt);
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
    public void TaskItemViewModel_ExposesScheduledTime()
    {
        var task = ScheduledTask(new TimeOnly(14, 5), 30);
        var vm = new TaskItemViewModel(task);

        Assert.True(vm.HasTime);
        Assert.Equal("14:05", vm.TimeText);
        Assert.StartsWith("14:05 · ", vm.TimeBadge);
        Assert.Contains("计划：14:05", vm.TooltipText);
        Assert.Contains("提前 30分钟 提醒", vm.TooltipText);
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
