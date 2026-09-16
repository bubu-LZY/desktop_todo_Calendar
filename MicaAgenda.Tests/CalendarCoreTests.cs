using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Tests;

public sealed class CalendarCoreTests
{
    [Fact]
    public void BuildMonth_ReturnsSundayFirstSixWeekGridWithTodayFlag()
    {
        var days = CalendarService.BuildMonth(new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 5));

        Assert.Equal(42, days.Count);
        Assert.Equal(new DateOnly(2026, 8, 30), days[0].Date);
        Assert.Equal(new DateOnly(2026, 10, 10), days[^1].Date);
        Assert.False(days[0].IsInCurrentMonth);
        Assert.True(days.Single(day => day.Date == new DateOnly(2026, 9, 5)).IsToday);
    }

    [Fact]
    public void BuildWeek_ReturnsSundayToSaturdayForSelectedDate()
    {
        var days = CalendarService.BuildWeek(new DateOnly(2026, 5, 13), new DateOnly(2026, 5, 10));

        Assert.Equal(7, days.Count);
        Assert.Equal(new DateOnly(2026, 5, 10), days[0].Date);
        Assert.Equal(new DateOnly(2026, 5, 16), days[^1].Date);
        Assert.True(days[0].IsToday);
    }

    [Fact]
    public void CalendarTask_MarkCompletedAndIncompleteUpdatesCompletionState()
    {
        var task = new CalendarTask
        {
            Id = Guid.Parse("15f09ef2-68f7-44be-8d5a-1c932ad1705c"),
            Date = new DateOnly(2026, 5, 10),
            Title = "整理实验数据",
            CreatedAt = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero)
        };
        var completedAt = new DateTimeOffset(2026, 5, 10, 18, 30, 0, TimeSpan.Zero);

        task.MarkCompleted(completedAt);

        Assert.True(task.IsCompleted);
        Assert.Equal(completedAt, task.CompletedAt);

        task.MarkIncomplete();

        Assert.False(task.IsCompleted);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public async Task CalendarDataStore_RoundTripsTasksAndSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-{Guid.NewGuid():N}.json");
        var store = new CalendarDataStore(path);
        var data = new CalendarData
        {
            Settings = new CalendarSettings
            {
                ViewMode = CalendarViewMode.Week,
                BackgroundMode = CalendarBackgroundMode.AcrylicMint,
                Opacity = 0.72,
                CellScale = 1.24,
                CellHeight = 92,
                AlwaysOnTop = true,
                AutoStart = true,
                WindowBounds = new WindowBounds(20, 30, 900, 650),
                MonthWindowBounds = new WindowBounds(25, 35, 910, 520),
                WeekWindowBounds = new WindowBounds(30, 40, 920, 360),
                YearWindowBounds = new WindowBounds(35, 45, 930, 760)
            },
            Tasks =
            [
                new CalendarTask
                {
                    Id = Guid.Parse("6f6c3736-ea1b-461a-bba1-9c624dd1b11a"),
                    Date = new DateOnly(2026, 5, 10),
                    Title = "写周计划",
                    IsCompleted = true,
                    IsImportant = true,
                    CreatedAt = new DateTimeOffset(2026, 5, 10, 8, 0, 0, TimeSpan.Zero),
                    CompletedAt = new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero)
                }
            ]
        };

        await store.SaveAsync(data);
        var loaded = await store.LoadAsync();

        Assert.Equal(CalendarViewMode.Week, loaded.Settings.ViewMode);
        Assert.Equal(CalendarBackgroundMode.AcrylicMint, loaded.Settings.BackgroundMode);
        Assert.Equal(0.72, loaded.Settings.Opacity);
        Assert.Equal(1.24, loaded.Settings.CellScale);
        Assert.Equal(92, loaded.Settings.CellHeight);
        Assert.True(loaded.Settings.AlwaysOnTop);
        Assert.True(loaded.Settings.AutoStart);
        Assert.Equal(new WindowBounds(20, 30, 900, 650), loaded.Settings.WindowBounds);
        Assert.Equal(new WindowBounds(25, 35, 910, 520), loaded.Settings.MonthWindowBounds);
        Assert.Equal(new WindowBounds(30, 40, 920, 360), loaded.Settings.WeekWindowBounds);
        Assert.Equal(new WindowBounds(35, 45, 930, 760), loaded.Settings.YearWindowBounds);
        var task = Assert.Single(loaded.Tasks);
        Assert.Equal("写周计划", task.Title);
        Assert.True(task.IsCompleted);
        Assert.True(task.IsImportant);
        Assert.Equal(new DateOnly(2026, 5, 10), task.Date);

        File.Delete(path);
    }

    [Fact]
    public async Task CalendarDataStore_SerializesRapidSavesAndKeepsReadableTaskData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-rapid-save-{Guid.NewGuid():N}.json");
        var store = new CalendarDataStore(path);
        var saves = Enumerable.Range(0, 24)
            .Select(index => store.SaveAsync(new CalendarData
            {
                Tasks =
                [
                    new CalendarTask
                    {
                        Id = Guid.CreateVersion7(),
                        Date = new DateOnly(2026, 5, 12),
                        Title = $"快速保存任务 {index:D2}",
                        CreatedAt = new DateTimeOffset(2026, 5, 12, 9, index, 0, TimeSpan.Zero)
                    }
                ]
            }));

        await Task.WhenAll(saves);
        var loaded = await store.LoadAsync();

        var task = Assert.Single(loaded.Tasks);
        Assert.StartsWith("快速保存任务 ", task.Title);

        File.Delete(path);
    }

    [Fact]
    public async Task CalendarDataStore_LoadsLegacyTaskWithImportantDefaultFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-legacy-{Guid.NewGuid():N}.json");
        var json = """
            {
              "Settings": {
                "ViewMode": "Month",
                "BackgroundMode": "Glass",
                "Opacity": 0.86,
                "CellScale": 1,
                "CellHeight": 74,
                "AlwaysOnTop": false,
                "AutoStart": false,
                "WindowBounds": {
                  "Left": 120,
                  "Top": 90,
                  "Width": 980,
                  "Height": 680
                }
              },
              "Tasks": [
                {
                  "Id": "29fe3f8f-ec14-444f-a9bd-b8796427f293",
                  "Date": "2026-05-10",
                  "Title": "旧任务",
                  "IsCompleted": false,
                  "CreatedAt": "2026-05-10T08:00:00+00:00",
                  "CompletedAt": null
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, json);
        var store = new CalendarDataStore(path);

        var loaded = await store.LoadAsync();

        var task = Assert.Single(loaded.Tasks);
        Assert.Equal("旧任务", task.Title);
        Assert.False(task.IsImportant);

        File.Delete(path);
    }

    [Fact]
    public async Task CalendarDataStore_MapsLegacyClearBorderBackgroundToNone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-clear-border-{Guid.NewGuid():N}.json");
        var json = """
            {
              "Settings": {
                "ViewMode": "Month",
                "BackgroundMode": "ClearBorder",
                "Opacity": 0.86,
                "CellScale": 1,
                "CellHeight": 74,
                "AlwaysOnTop": false,
                "AutoStart": false,
                "WindowBounds": {
                  "Left": 120,
                  "Top": 90,
                  "Width": 980,
                  "Height": 680
                }
              },
              "Tasks": []
            }
            """;
        await File.WriteAllTextAsync(path, json);
        var store = new CalendarDataStore(path);

        var loaded = await store.LoadAsync();

        Assert.Equal(CalendarBackgroundMode.None, loaded.Settings.BackgroundMode);

        File.Delete(path);
    }

    [Fact]
    public async Task ChinaHolidayService_RoundTripsCacheAndProvidesEmbedded2026Holidays()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-holidays-{Guid.NewGuid():N}.json");
        var service = new ChinaHolidayService(path);
        var cache = new ChinaHolidayCache
        {
            Source = "test",
            UpdatedAt = new DateTimeOffset(2026, 5, 10, 8, 0, 0, TimeSpan.Zero),
            Holidays =
            [
                new ChinaHoliday
                {
                    Date = new DateOnly(2026, 5, 1),
                    Name = "劳动节",
                    IsHoliday = true,
                    IsMakeupWorkday = false,
                    Source = "test",
                    UpdatedAt = new DateTimeOffset(2026, 5, 10, 8, 0, 0, TimeSpan.Zero)
                }
            ]
        };

        await service.SaveCacheAsync(cache);
        var loaded = await service.LoadCacheAsync();
        var embedded = ChinaHolidayService.GetEmbeddedHolidays(2026);

        Assert.Equal("test", loaded.Source);
        Assert.Single(loaded.Holidays);
        Assert.Contains(embedded, holiday => holiday.Date == new DateOnly(2026, 5, 1) && holiday.Name == "劳动节" && holiday.IsHoliday);
        Assert.Contains(embedded, holiday => holiday.Date == new DateOnly(2026, 5, 9) && holiday.Name == "劳动节后补班" && holiday.IsMakeupWorkday);

        File.Delete(path);
    }

    // ===== 多选提醒（含「提前一天」）=====

    [Fact]
    public void SetReminderLeads_FirstBecomesPrimary_RestGoToAdditional()
    {
        var task = new CalendarTask { Date = new DateOnly(2026, 9, 20), Time = new TimeOnly(10, 0), Title = "t" };

        task.SetReminderLeads([30, 0, 1440]);

        Assert.Equal(30, task.ReminderLeadMinutes);
        Assert.Equal([0, 1440], task.AdditionalReminderLeadMinutes);
        Assert.Equal([30, 0, 1440], task.AllReminderLeads.ToArray());

        // 空集合 = 不提醒，两处都清空
        task.SetReminderLeads([]);
        Assert.Null(task.ReminderLeadMinutes);
        Assert.Null(task.AdditionalReminderLeadMinutes);
        Assert.False(task.HasReminders);
    }

    [Fact]
    public void DueReminderLeads_FiresEachSelectedLeadIndependently()
    {
        var task = new CalendarTask { Date = new DateOnly(2026, 9, 20), Time = new TimeOnly(10, 0), Title = "t" };
        task.SetReminderLeads([1440, 0]); // 提前一天 + 到时提醒

        // 9/19 10:00：提前一天刚到点，到时提醒还没到
        var dueEarly = task.DueReminderLeads(new DateTime(2026, 9, 19, 10, 0, 0));
        Assert.Equal([1440], dueEarly.ToArray());

        // 推过 1440 档位后，同刻再轮询不重复推
        task.MarkReminderSent(1440, DateTimeOffset.Now);
        Assert.Empty(task.DueReminderLeads(new DateTime(2026, 9, 19, 10, 1, 0)));

        // 9/20 10:00：到时提醒到点，与已推的 1440 互不挡
        var dueOnTime = task.DueReminderLeads(new DateTime(2026, 9, 20, 10, 0, 0));
        Assert.Equal([0], dueOnTime.ToArray());
    }

    [Fact]
    public void OneDayLead_TriggersExactly24HoursBeforeScheduledAt()
    {
        var task = new CalendarTask { Date = new DateOnly(2026, 9, 20), Time = new TimeOnly(9, 0), Title = "t" };
        task.SetReminderLeads([1440]);

        var trigger = Assert.Single(task.ReminderTriggers());
        Assert.Equal(1440, trigger.Lead);
        Assert.Equal(new DateTime(2026, 9, 19, 9, 0, 0), trigger.TriggerAt);
        Assert.Equal(new DateTime(2026, 9, 19, 9, 0, 0), task.EarliestReminderTriggerAt());
    }

    [Fact]
    public void ResetReminder_ClearsFiredMarksForEveryLead()
    {
        var task = new CalendarTask { Date = new DateOnly(2026, 9, 20), Time = new TimeOnly(10, 0), Title = "t" };
        task.SetReminderLeads([30, 0]);
        task.MarkReminderSent(30, DateTimeOffset.Now);
        task.MarkReminderSent(0, DateTimeOffset.Now);

        task.ResetReminder();

        Assert.Null(task.ReminderSentAt);
        Assert.Null(task.FiredReminderLeads);
        Assert.Equal([30, 0], task.DueReminderLeads(new DateTime(2026, 9, 20, 10, 0, 0)).ToArray());
    }

    [Fact]
    public async Task CalendarDataStore_RoundTripsMultipleReminderLeads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-multi-reminder-{Guid.NewGuid():N}.json");
        var store = new CalendarDataStore(path);
        var task = new CalendarTask
        {
            Date = new DateOnly(2026, 9, 20),
            Time = new TimeOnly(14, 30),
            Title = "多提醒任务",
            ReminderLeadMinutes = 0,
            AdditionalReminderLeadMinutes = [30, 1440],
            CreatedAt = new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero)
        };
        await store.SaveAsync(new CalendarData { Tasks = [task] });

        var loaded = (await store.LoadAsync()).Tasks.Single();

        Assert.Equal(new TimeOnly(14, 30), loaded.Time);
        Assert.Equal([0, 30, 1440], loaded.AllReminderLeads.ToArray());

        File.Delete(path);
    }
}
