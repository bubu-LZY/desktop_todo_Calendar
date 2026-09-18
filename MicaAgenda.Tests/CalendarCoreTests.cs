using System.IO;
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
    public async Task CalendarDataStore_CorruptFile_QuarantinesAndThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mica-agenda-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "calendar-data.json");
        await File.WriteAllTextAsync(path, "{ 这不是合法 JSON ");

        try
        {
            var store = new CalendarDataStore(path);

            // 损坏文件绝不能静默返回空数据（那会被自动保存覆盖成空文件），必须抛专用异常。
            await Assert.ThrowsAsync<CalendarDataLoadException>(() => store.LoadAsync());

            // 坏文件必须被改名隔离，而不是留在原位等下一次保存覆盖。
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(dir, "calendar-data.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CalendarDataStore_MissingFile_ReturnsEmptyData()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mica-agenda-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "calendar-data.json");

        try
        {
            var store = new CalendarDataStore(path);
            var data = await store.LoadAsync();

            // 首次启动（文件不存在）必须返回空数据、不抛异常 —— 与损坏文件严格区分。
            Assert.NotNull(data);
            Assert.Empty(data.Tasks);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

    // ===== 周期任务：物化展开 =====

    [Fact]
    public void RecurrenceService_ExpandDaily_GeneratesConsecutiveDays()
    {
        var master = new CalendarTask
        {
            Id = Guid.NewGuid(),
            Date = new DateOnly(2026, 9, 1),
            Title = "吃药",
            Recurrence = RecurrenceFrequency.Daily,
            RecurrenceInterval = 1,
            RecurrenceEnd = new DateOnly(2026, 9, 5),
        };

        var instances = RecurrenceService.Expand(master);

        // 模板本身是 9/1，实例从 9/2 到 9/5（含结束日期），共 4 个。
        Assert.Equal(4, instances.Count);
        Assert.Equal(new DateOnly(2026, 9, 2), instances[0].Date);
        Assert.Equal(new DateOnly(2026, 9, 5), instances[^1].Date);
        // 所有实例都指向源任务，且各自 Id 独立。
        Assert.All(instances, i => Assert.Equal(master.Id, i.SeriesId));
        Assert.Equal(instances.Count, instances.Select(i => i.Id).Distinct().Count());
        Assert.All(instances, i => Assert.Equal("吃药", i.Title));
    }

    [Fact]
    public void RecurrenceService_ExpandWeekly_StepsSevenDays()
    {
        var master = new CalendarTask
        {
            Date = new DateOnly(2026, 9, 1),
            Title = "组会",
            Recurrence = RecurrenceFrequency.Weekly,
            RecurrenceInterval = 1,
            RecurrenceEnd = new DateOnly(2026, 9, 15),
        };

        var instances = RecurrenceService.Expand(master);

        Assert.Equal(2, instances.Count); // 9/8, 9/15
        Assert.Equal(new DateOnly(2026, 9, 8), instances[0].Date);
        Assert.Equal(new DateOnly(2026, 9, 15), instances[1].Date);
    }

    [Fact]
    public void RecurrenceService_ExpandMonthly_KeepsDayOfMonth()
    {
        var master = new CalendarTask
        {
            Date = new DateOnly(2026, 1, 15),
            Title = "交房租",
            Recurrence = RecurrenceFrequency.Monthly,
            RecurrenceInterval = 1,
            RecurrenceEnd = new DateOnly(2026, 3, 15),
        };

        var instances = RecurrenceService.Expand(master);

        Assert.Equal(2, instances.Count); // 2/15, 3/15
        Assert.Equal(new DateOnly(2026, 2, 15), instances[0].Date);
        Assert.Equal(new DateOnly(2026, 3, 15), instances[1].Date);
    }

    [Fact]
    public void RecurrenceService_ExpandWithoutEnd_CapsAtTwoYears()
    {
        var master = new CalendarTask
        {
            Date = new DateOnly(2026, 1, 1),
            Title = "打卡",
            Recurrence = RecurrenceFrequency.Daily,
            RecurrenceInterval = 1,
            RecurrenceEnd = null,
        };

        var instances = RecurrenceService.Expand(master);

        // 封顶 MaxMaterializedDays 天：从 1/2 起到 1/1 + 730 天（不含模板本身）
        Assert.Equal(RecurrenceService.MaxMaterializedDays, instances.Count);
        Assert.Equal(new DateOnly(2026, 1, 2), instances[0].Date);
    }

    [Fact]
    public void RecurrenceService_ExpandNone_ReturnsEmpty()
    {
        var master = new CalendarTask
        {
            Date = new DateOnly(2026, 9, 1),
            Title = "普通任务",
            Recurrence = RecurrenceFrequency.None,
        };

        Assert.Empty(RecurrenceService.Expand(master));
    }

    [Fact]
    public async Task CalendarDataStore_RoundTripsRecurrenceFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mica-agenda-recur-{Guid.NewGuid():N}.json");
        var store = new CalendarDataStore(path);
        var master = new CalendarTask
        {
            Id = Guid.NewGuid(),
            Date = new DateOnly(2026, 9, 1),
            Title = "周报",
            Recurrence = RecurrenceFrequency.Weekly,
            RecurrenceInterval = 2,
            RecurrenceEnd = new DateOnly(2026, 12, 31),
            CreatedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
        };
        await store.SaveAsync(new CalendarData { Tasks = [master] });

        var loaded = (await store.LoadAsync()).Tasks.Single();

        Assert.Equal(RecurrenceFrequency.Weekly, loaded.Recurrence);
        Assert.Equal(2, loaded.RecurrenceInterval);
        Assert.Equal(new DateOnly(2026, 12, 31), loaded.RecurrenceEnd);

        File.Delete(path);
    }
}
