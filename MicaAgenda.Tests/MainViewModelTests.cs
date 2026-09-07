using MicaAgenda.App.Models;
using MicaAgenda.App.ViewModels;

namespace MicaAgenda.Tests;

public sealed class MainViewModelTests
{
    /// <summary>
    /// 默认视图是月视图，月视图的日期格子位于连续时间轴 TimelineMonths 中
    /// （VisibleDays 只在周视图使用）。取全部时间轴格子作为断言集合。
    /// </summary>
    private static IEnumerable<DayCellViewModel> MonthDays(MainViewModel viewModel)
    {
        return viewModel.TimelineMonths.SelectMany(block => block.Days);
    }

    [Fact]
    public void AddTask_CreatesTaskForSelectedDateAndRefreshesDayCell()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.AddTask(new DateOnly(2026, 5, 12), "准备组会汇报");

        var task = Assert.Single(data.Tasks);
        Assert.Equal(new DateOnly(2026, 5, 12), task.Date);
        Assert.Equal("准备组会汇报", task.Title);
        Assert.False(task.IsCompleted);
        Assert.Contains(MonthDays(viewModel), day => day.Date == task.Date && day.Tasks.Any(item => item.Title == task.Title));
    }

    [Fact]
    public void ToggleTaskCompletion_UpdatesTaskAndDisplayState()
    {
        var task = new CalendarTask
        {
            Id = Guid.Parse("bfec2669-e98e-482c-a57f-9a171ddf7261"),
            Date = new DateOnly(2026, 5, 10),
            Title = "检查模拟结果",
            CreatedAt = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero)
        };
        var data = new CalendarData { Tasks = [task] };
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 17, 0, 0, TimeSpan.Zero));

        viewModel.ToggleTaskCompletion(task.Id);

        Assert.True(task.IsCompleted);
        Assert.Equal(new DateTimeOffset(2026, 5, 10, 17, 0, 0, TimeSpan.Zero), task.CompletedAt);
        Assert.True(MonthDays(viewModel).SelectMany(day => day.Tasks).Single(item => item.Id == task.Id).IsCompleted);

        viewModel.ToggleTaskCompletion(task.Id);

        Assert.False(task.IsCompleted);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void ClearOverdueTasks_RemovesOnlyOverdueUncompleted()
    {
        var now = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero);
        var data = new CalendarData
        {
            Tasks =
            [
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "逾期1", CreatedAt = now },
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "逾期已完成", IsCompleted = true, CreatedAt = now },
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 15), Title = "今日", CreatedAt = now },
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 16), Title = "未来", CreatedAt = now },
            ]
        };
        var viewModel = new MainViewModel(data, () => now);

        var removed = viewModel.ClearOverdueTasks();

        Assert.Equal(1, removed);
        Assert.Equal(3, data.Tasks.Count);
        Assert.DoesNotContain(data.Tasks, t => t.Title == "逾期1");
    }

    [Fact]
    public void MarkOpenCompleted_MarksOnlyThisWeeksOpenAsCompleted()
    {
        var now = new DateTimeOffset(2026, 5, 13, 10, 0, 0, TimeSpan.Zero);
        var data = new CalendarData
        {
            Tasks =
            [
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "逾期未完成", CreatedAt = now },
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 13), Title = "本周", CreatedAt = now },
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 16), Title = "本周2", CreatedAt = now },
            ]
        };
        var viewModel = new MainViewModel(data, () => now);

        var marked = viewModel.MarkOpenCompleted();

        Assert.Equal(2, marked);
        Assert.True(data.Tasks.Single(t => t.Title == "本周").IsCompleted);
        Assert.True(data.Tasks.Single(t => t.Title == "本周2").IsCompleted);
        Assert.False(data.Tasks.Single(t => t.Title == "逾期未完成").IsCompleted);
    }

    [Fact]
    public void DeleteAllTasks_ClearsEverything()
    {
        var data = new CalendarData
        {
            Tasks =
            [
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "a" },
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 11), Title = "b" },
            ]
        };
        var viewModel = new MainViewModel(data, () => DateTimeOffset.Now);

        var removed = viewModel.DeleteAllTasks();

        Assert.Equal(2, removed);
        Assert.Empty(data.Tasks);
    }

    [Fact]
    public void ToggleTaskImportance_UpdatesOnlyTheSelectedTask()
    {
        var first = new CalendarTask
        {
            Id = Guid.Parse("f51f8a5c-2a2a-4310-8d18-c1182051d695"),
            Date = new DateOnly(2026, 5, 10),
            Title = "重要任务",
            CreatedAt = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero)
        };
        var second = new CalendarTask
        {
            Id = Guid.Parse("8c0d74e6-0fc4-48a7-8780-16d144c89e89"),
            Date = new DateOnly(2026, 5, 10),
            Title = "普通任务",
            CreatedAt = new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero)
        };
        var data = new CalendarData { Tasks = [first, second] };
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 17, 0, 0, TimeSpan.Zero));

        viewModel.ToggleTaskImportance(first.Id);

        Assert.True(first.IsImportant);
        Assert.False(second.IsImportant);
        Assert.True(MonthDays(viewModel).SelectMany(day => day.Tasks).Single(item => item.Id == first.Id).IsImportant);
        Assert.False(MonthDays(viewModel).SelectMany(day => day.Tasks).Single(item => item.Id == second.Id).IsImportant);
    }

    [Fact]
    public void SetViewMode_RebuildsVisibleCalendar()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.SetViewMode(CalendarViewMode.Week);

        Assert.Equal(CalendarViewMode.Week, data.Settings.ViewMode);
        Assert.Equal(7, viewModel.VisibleDays.Count);

        viewModel.SetViewMode(CalendarViewMode.Year);

        Assert.Equal(CalendarViewMode.Year, data.Settings.ViewMode);
        Assert.Equal(12, viewModel.YearMonths.Count);
    }

    [Fact]
    public void RefreshHeightProperties_DoesNotRebuildVisibleDayCells()
    {
        var data = new CalendarData
        {
            Settings = new CalendarSettings
            {
                CellHeight = 74
            }
        };
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        var firstDay = MonthDays(viewModel).First();
        var originalCount = MonthDays(viewModel).Count();

        data.Settings.CellHeight = 120;
        viewModel.RefreshHeightProperties();

        Assert.Equal(originalCount, MonthDays(viewModel).Count());
        Assert.Same(firstDay, MonthDays(viewModel).First());
        Assert.Equal(120, viewModel.MonthCellHeight);
        Assert.Equal(45.6, viewModel.YearCellHeight, 1);

        // 周视图格子高度是独立可调的（拖右下角手柄改的是 WeekCellHeight），
        // 刻意不再跟着月视图的 CellHeight 走 —— 这里断言它保持不变。
        // （早期版本里它是 CellHeight 的派生值，测试曾断言 216；行为改了之后断言要跟着改。）
        Assert.Equal(122, viewModel.WeekCellHeight);
    }

    [Fact]
    public void RenameTask_UpdatesExistingTaskWithoutAddingAnotherTask()
    {
        var task = new CalendarTask
        {
            Id = Guid.Parse("75df4319-6154-4f34-b340-371e35b68e79"),
            Date = new DateOnly(2026, 5, 10),
            Title = "旧任务",
            CreatedAt = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero)
        };
        var data = new CalendarData { Tasks = [task] };
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

        viewModel.RenameTask(task.Id, "新任务");

        var renamed = Assert.Single(data.Tasks);
        Assert.Equal(task.Id, renamed.Id);
        Assert.Equal("新任务", renamed.Title);
        Assert.Single(MonthDays(viewModel).SelectMany(day => day.Tasks), item => item.Id == task.Id);
        Assert.Equal("新任务", MonthDays(viewModel).SelectMany(day => day.Tasks).Single(item => item.Id == task.Id).Title);
    }

    [Fact]
    public void DeleteTask_RemovesOnlyTheSelectedTask()
    {
        var first = new CalendarTask
        {
            Id = Guid.Parse("cf74813b-8769-4823-b91f-2b1da8fdd85f"),
            Date = new DateOnly(2026, 5, 3),
            Title = "要删除",
            CreatedAt = new DateTimeOffset(2026, 5, 3, 9, 0, 0, TimeSpan.Zero)
        };
        var second = new CalendarTask
        {
            Id = Guid.Parse("9d4021a9-d8b9-4743-a04d-566dfaa25f89"),
            Date = new DateOnly(2026, 5, 3),
            Title = "保留",
            CreatedAt = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero)
        };
        var data = new CalendarData { Tasks = [first, second] };
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 3, 11, 0, 0, TimeSpan.Zero));

        viewModel.DeleteTask(first.Id);

        var remaining = Assert.Single(data.Tasks);
        Assert.Equal(second.Id, remaining.Id);
        Assert.DoesNotContain(MonthDays(viewModel).SelectMany(day => day.Tasks), task => task.Id == first.Id);
        Assert.Contains(MonthDays(viewModel).SelectMany(day => day.Tasks), task => task.Id == second.Id);
    }

    [Fact]
    public void Holidays_AppearOnMatchingDayCells()
    {
        var holidays = new[]
        {
            new ChinaHoliday
            {
                Date = new DateOnly(2026, 5, 1),
                Name = "劳动节",
                IsHoliday = true,
                Source = "test",
                UpdatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)
            },
            new ChinaHoliday
            {
                Date = new DateOnly(2026, 5, 9),
                Name = "劳动节后补班",
                IsHoliday = false,
                IsMakeupWorkday = true,
                Source = "test",
                UpdatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)
            }
        };
        var viewModel = new MainViewModel(
            new CalendarData(),
            () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero),
            holidays);

        // 相邻月份块会补位显示上下月的日期（如 5/1 同时出现在 4 月块和 5 月块），
        // 因此这里必须限定到 5 月块本身，否则日期会重复。
        var may = viewModel.TimelineMonths.Single(block => block.Year == 2026 && block.Month == 5);
        var laborDay = may.Days.Single(day => day.Date == new DateOnly(2026, 5, 1));
        var makeupDay = may.Days.Single(day => day.Date == new DateOnly(2026, 5, 9));

        Assert.Equal("休 劳动节", Assert.Single(laborDay.Holidays).BadgeText);
        Assert.Equal("班 劳动节后补班", Assert.Single(makeupDay.Holidays).BadgeText);
    }

    // ===== 今日任务面板 =====

    [Fact]
    public void TodayTasks_OnlyContainsTasksOfToday()
    {
        var viewModel = new MainViewModel(
            new CalendarData(),
            () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        Assert.Empty(viewModel.TodayTasks);
        Assert.False(viewModel.HasTodayTasks);
        Assert.Equal("暂无任务", viewModel.TodayTaskSummary);

        viewModel.AddTask(new DateOnly(2026, 5, 10), "今天的事");
        viewModel.AddTask(new DateOnly(2026, 5, 12), "后天的事");

        var title = Assert.Single(viewModel.TodayTasks).Title;
        Assert.Equal("今天的事", title);
        Assert.True(viewModel.HasTodayTasks);
        Assert.Equal(1, viewModel.TodayTaskTotal);
        Assert.Equal(1, viewModel.TodayTaskPending);
        Assert.Equal("共 1 项 · 待办 1", viewModel.TodayTaskSummary);
    }

    [Fact]
    public void CommitTodayTask_AddsTaskForTodayAndClearsDraft()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.BeginAddTodayTask();
        Assert.True(viewModel.IsAddingTodayTask);

        viewModel.TodayTaskDraft = "  写周报  ";
        var created = viewModel.CommitTodayTask();

        Assert.NotNull(created);
        Assert.Equal(new DateOnly(2026, 5, 10), created.Date);
        Assert.Equal("写周报", created.Title);
        Assert.False(viewModel.IsAddingTodayTask);
        Assert.Equal(string.Empty, viewModel.TodayTaskDraft);
    }

    [Fact]
    public void CommitTodayTask_ReturnsNullForBlankDraft()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.BeginAddTodayTask();
        viewModel.TodayTaskDraft = "   ";

        Assert.Null(viewModel.CommitTodayTask());
        Assert.Empty(data.Tasks);
        Assert.False(viewModel.IsAddingTodayTask);
    }

    [Fact]
    public void CancelTodayTask_ClearsDraftWithoutAdding()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.BeginAddTodayTask();
        viewModel.TodayTaskDraft = "不想要了";
        viewModel.CancelTodayTask();

        Assert.Empty(data.Tasks);
        Assert.False(viewModel.IsAddingTodayTask);
        Assert.Equal(string.Empty, viewModel.TodayTaskDraft);
    }

    [Fact]
    public void TodayTasks_ReflectsCompletionAndPendingSummary()
    {
        var data = new CalendarData();
        var today = new DateOnly(2026, 5, 10);
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.AddTask(today, "A");
        var second = viewModel.AddTask(today, "B");
        viewModel.ToggleTaskCompletion(second.Id);

        Assert.Equal(2, viewModel.TodayTaskTotal);
        Assert.Equal(1, viewModel.TodayTaskPending);
        Assert.Equal("共 2 项 · 待办 1", viewModel.TodayTaskSummary);
    }

    [Fact]
    public void RefreshTodayTasks_PreservesEditingState()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        var task = viewModel.AddTask(new DateOnly(2026, 5, 10), "原标题");

        var item = viewModel.TodayTasks.Single(t => t.Id == task.Id);
        item.BeginEdit();
        item.EditTitle = "改到一半";

        // 模拟周期性刷新：重建 TodayTasks 的 ViewModel，但编辑态必须保留
        viewModel.RefreshTodayTasks();

        var refreshed = viewModel.TodayTasks.Single(t => t.Id == task.Id);
        Assert.True(refreshed.IsEditing);
        Assert.Equal("改到一半", refreshed.EditTitle);
        Assert.Equal("原标题", refreshed.Title);
    }

    // ===== 日期格子选中 =====

    [Fact]
    public void SelectCell_MarksMatchingDayCellsAndMovesSelection()
    {
        var viewModel = new MainViewModel(
            new CalendarData(),
            () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var target = new DateOnly(2026, 5, 12);
        viewModel.SelectCell(target);

        Assert.Equal(target, viewModel.SelectedCellDate);
        Assert.True(MonthDays(viewModel).Single(day => day.Date == target).IsSelected);

        var another = new DateOnly(2026, 5, 20);
        viewModel.SelectCell(another);

        Assert.Equal(another, viewModel.SelectedCellDate);
        Assert.False(MonthDays(viewModel).Single(day => day.Date == target).IsSelected);
        Assert.True(MonthDays(viewModel).Single(day => day.Date == another).IsSelected);
    }

    [Fact]
    public void ClearCellSelection_RemovesHighlight()
    {
        var viewModel = new MainViewModel(
            new CalendarData(),
            () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var target = new DateOnly(2026, 5, 12);
        viewModel.SelectCell(target);
        viewModel.ClearCellSelection();

        Assert.Null(viewModel.SelectedCellDate);
        Assert.DoesNotContain(MonthDays(viewModel), day => day.IsSelected);
    }

    [Fact]
    public void SelectionSurvivesCalendarRebuild()
    {
        var viewModel = new MainViewModel(
            new CalendarData(),
            () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var target = new DateOnly(2026, 5, 12);
        viewModel.SelectCell(target);

        // 新建任务会触发整表重建；选中态存在 ViewModel 层，重建后应自动恢复
        viewModel.AddTask(target, "重建后仍应高亮");

        Assert.Equal(target, viewModel.SelectedCellDate);
        Assert.True(MonthDays(viewModel).Single(day => day.Date == target).IsSelected);
    }

    [Fact]
    public void SelectCell_SwitchesPanelToThatDaysTasks()
    {
        var viewModel = new MainViewModel(
            new CalendarData(),
            () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var panelTarget = new DateOnly(2026, 5, 12);
        viewModel.AddTask(new DateOnly(2026, 5, 10), "今天的事");
        viewModel.AddTask(panelTarget, "12号的事");

        viewModel.SelectCell(panelTarget);

        // 面板应切换到选中日期，而非始终停留在今日
        Assert.Equal(panelTarget, viewModel.PanelDate);
        Assert.Equal("5月12日任务", viewModel.PanelHeader);
        Assert.Equal(panelTarget, Assert.Single(viewModel.TodayTasks).Date);
        Assert.Equal("12号的事", viewModel.TodayTasks.Single().Title);

        var another = new DateOnly(2026, 5, 20);
        viewModel.SelectCell(another);

        Assert.Equal(another, viewModel.PanelDate);
        Assert.Empty(viewModel.TodayTasks);
        Assert.False(viewModel.HasTodayTasks);
    }

    [Fact]
    public void ClearCellSelection_PanelReturnsToToday()
    {
        var viewModel = new MainViewModel(
            new CalendarData(),
            () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        viewModel.AddTask(new DateOnly(2026, 5, 10), "今天的事");
        viewModel.SelectCell(new DateOnly(2026, 5, 12));
        Assert.Equal("5月12日任务", viewModel.PanelHeader);

        viewModel.ClearCellSelection();

        Assert.Equal(new DateOnly(2026, 5, 10), viewModel.PanelDate);
        Assert.Equal("今日任务", viewModel.PanelHeader);
        Assert.Equal("今天的事", viewModel.TodayTasks.Single().Title);
    }

    [Fact]
    public void CommitTodayTask_WhenPanelFollowsSelectedDate_AddsTaskForThatDate()
    {
        var data = new CalendarData();
        var viewModel = new MainViewModel(data, () => new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var panelTarget = new DateOnly(2026, 5, 15);
        viewModel.SelectCell(panelTarget);

        viewModel.BeginAddTodayTask();
        viewModel.TodayTaskDraft = "选中日的任务";
        var created = viewModel.CommitTodayTask();

        Assert.NotNull(created);
        Assert.Equal(panelTarget, created.Date);
        Assert.Equal(panelTarget, Assert.Single(viewModel.TodayTasks).Date);
    }

    [Fact]
    public void PanelHeader_FollowsTodayOnDayRolloverOnlyWhenNotLocked()
    {
        var now = new DateTimeOffset(new DateTime(2026, 5, 10, 23, 30, 0));
        var viewModel = new MainViewModel(new CalendarData(), () => now);

        Assert.Equal("今日任务", viewModel.PanelHeader);

        // 选中一个具体日期：面板锁定到该日期，跨天不跟随
        var target = new DateOnly(2026, 5, 12);
        viewModel.SelectCell(target);

        now = now.AddDays(3);
        viewModel.RefreshClock();

        Assert.Equal(target, viewModel.PanelDate);
        Assert.Equal("5月12日任务", viewModel.PanelHeader);

        // 未选中日期时，跨天面板自动跟随新的今日
        viewModel.ClearCellSelection();
        viewModel.RefreshClock();

        Assert.Equal(new DateOnly(2026, 5, 13), viewModel.PanelDate);
        Assert.Equal("今日任务", viewModel.PanelHeader);
    }

    [Fact]
    public void RefreshClock_SkipsRebuildWhileInlineInputPending()
    {
        var now = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
        var viewModel = new MainViewModel(new CalendarData(), () => now);

        var cell = MonthDays(viewModel).First();
        var date = cell.Date;
        cell.BeginAdd();
        cell.DraftTitle = "还没写完";

        // 同一天、有未提交草稿：不应重建，草稿必须还在
        viewModel.RefreshClock();

        Assert.True(MonthDays(viewModel).Single(day => day.Date == date).IsAddingTask);
        Assert.Equal("还没写完", MonthDays(viewModel).Single(day => day.Date == date).DraftTitle);
    }

    [Fact]
    public void DeleteTask_RemovesFromTodayTasksAndCalendarCells()
    {
        var now = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
        var task1 = new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "A", CreatedAt = now };
        var task2 = new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "B", CreatedAt = now };
        var data = new CalendarData { Tasks = [task1, task2] };
        var viewModel = new MainViewModel(data, () => now);

        // 删之前：所有 VM 集合都包含两条
        Assert.Equal(2, viewModel.TodayTasks.Count);
        var targetCell = MonthDays(viewModel).Single(day => day.Date == new DateOnly(2026, 5, 10));
        Assert.Equal(2, targetCell.Tasks.Count);

        viewModel.DeleteTask(task1.Id);

        // 删之后：所有 VM 集合必须同步只剩 task2
        Assert.Single(data.Tasks);
        Assert.Equal(task2.Id, Assert.Single(data.Tasks).Id);
        Assert.Single(viewModel.TodayTasks);
        Assert.Equal(task2.Id, Assert.Single(viewModel.TodayTasks).Id);

        var afterCell = MonthDays(viewModel).Single(day => day.Date == new DateOnly(2026, 5, 10));
        Assert.Single(afterCell.Tasks);
        Assert.Equal(task2.Id, Assert.Single(afterCell.Tasks).Id);
    }

    [Fact]
    public void DeleteAllTasks_ClearsTodayAndWeekAndCells()
    {
        var now = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
        var data = new CalendarData
        {
            Tasks =
            [
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "a", CreatedAt = now },
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 11), Title = "b", CreatedAt = now },
            ]
        };
        var viewModel = new MainViewModel(data, () => now);

        Assert.Single(viewModel.TodayTasks);

        viewModel.DeleteAllTasks();

        Assert.Empty(data.Tasks);
        Assert.Empty(viewModel.TodayTasks);
        Assert.All(MonthDays(viewModel), cell => Assert.Empty(cell.Tasks));
    }

    // ===== 左右侧同步回归测试 =====
    //
    // 日历格子里的 TaskItemViewModel 与右侧面板里的不是同一批实例，
    // 底层 CalendarTask 又是 POCO（不发通知）。任一侧改了完成状态，
    // 都必须由刷新流程把新状态推到另一侧，否则就会出现
    // "右侧勾完，月份小格子不更新" / "取消已完成不会挪回未完成"。

    /// <summary>在右侧今日任务面板里勾选完成，月份小格子里的任务必须同步变成已完成。</summary>
    [Fact]
    public void ToggleFromTodayPanel_UpdatesDayCellTaskState()
    {
        var now = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var data = new CalendarData
        {
            Tasks =
            [
                new CalendarTask { Id = id, Date = new DateOnly(2026, 5, 10), Title = "写周报", CreatedAt = now },
            ]
        };
        var viewModel = new MainViewModel(data, () => now);

        var cell = MonthDays(viewModel).Single(day => day.Date == new DateOnly(2026, 5, 10));
        Assert.False(Assert.Single(cell.Tasks).IsCompleted);

        // 走右侧面板的入口（面板 VM 与格子 VM 是不同的实例）
        viewModel.ToggleTaskCompletion(id);

        Assert.True(Assert.Single(cell.Tasks).IsCompleted);
        Assert.True(Assert.Single(viewModel.TodayTasks).IsCompleted);
    }

    /// <summary>取消已完成后，任务要从"已完成"分组挪回"未完成"分组。</summary>
    [Fact]
    public void ToggleCompletion_MovesTaskBetweenOpenAndCompletedGroups()
    {
        var now = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var data = new CalendarData
        {
            Tasks =
            [
                new CalendarTask { Id = id, Date = new DateOnly(2026, 5, 10), Title = "写周报", CreatedAt = now },
            ]
        };
        var viewModel = new MainViewModel(data, () => now);

        Assert.Single(viewModel.TodayOpenTasks);
        Assert.Empty(viewModel.TodayCompletedTasks);

        viewModel.ToggleTaskCompletion(id);
        Assert.Empty(viewModel.TodayOpenTasks);
        Assert.Single(viewModel.TodayCompletedTasks);

        // 取消已完成：必须回到未完成分组，而不是两边都不在
        viewModel.ToggleTaskCompletion(id);
        Assert.Single(viewModel.TodayOpenTasks);
        Assert.Empty(viewModel.TodayCompletedTasks);
    }

    /// <summary>把一条历史逾期任务标记完成后，不能从三个本周分组里同时消失。</summary>
    [Fact]
    public void CompleteOverdueTask_DoesNotMakeItVanishFromWeekGroups()
    {
        var now = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var data = new CalendarData
        {
            Tasks =
            [
                // 计划日期早于本周（5/10 是周日，本周从 5/10 开始），属于"逾期未完成"
                new CalendarTask { Id = id, Date = new DateOnly(2026, 4, 20), Title = "交房租", CreatedAt = now },
            ]
        };
        var viewModel = new MainViewModel(data, () => now);

        Assert.Single(viewModel.WeekOverdueTasks);

        viewModel.ToggleTaskCompletion(id);

        var visible = viewModel.WeekOverdueTasks.Count
                      + viewModel.WeekOpenTasks.Count
                      + viewModel.WeekCompletedTasks.Count;
        Assert.Equal(1, visible);
        Assert.Single(viewModel.WeekCompletedTasks);
    }

    /// <summary>清空某一天的任务时，月份小格子里的残影也要一并清掉。</summary>
    [Fact]
    public void ClearPanelDateTasks_AlsoClearsDayCells()
    {
        var now = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);
        var data = new CalendarData
        {
            Tasks =
            [
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "a", CreatedAt = now },
                new CalendarTask { Id = Guid.NewGuid(), Date = new DateOnly(2026, 5, 10), Title = "b", CreatedAt = now },
            ]
        };
        var viewModel = new MainViewModel(data, () => now);

        var removed = viewModel.ClearPanelDateTasks();

        Assert.Equal(2, removed);
        Assert.Empty(viewModel.TodayTasks);
        var cell = MonthDays(viewModel).Single(day => day.Date == new DateOnly(2026, 5, 10));
        Assert.Empty(cell.Tasks);
        Assert.False(cell.HasTasks);
    }

    // ===== 时间追踪 =====

    [Fact]
    public void TaskTimeMetrics_ComputePendingOverdueAndDuration()
    {
        var created = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);
        var task = new CalendarTask
        {
            Date = new DateOnly(2026, 4, 20),
            Title = "交房租",
            CreatedAt = created
        };

        // 未完成：从创建日算起的未完成天数，以及相对计划日期的逾期天数
        Assert.Equal(5, task.GetPendingDays(new DateOnly(2026, 4, 6)));
        Assert.Equal(0, task.GetOverdueDays(new DateOnly(2026, 4, 6)));
        Assert.Equal(10, task.GetOverdueDays(new DateOnly(2026, 4, 30)));
        Assert.True(task.IsOverdue(new DateOnly(2026, 4, 30)));
        Assert.Null(task.GetCompletionDuration());

        // 超时完成：完成时刻晚于计划日
        task.MarkCompleted(new DateTimeOffset(2026, 4, 23, 10, 0, 0, TimeSpan.Zero));
        Assert.True(task.IsCompletedLate());
        Assert.Equal(3, task.GetCompletedLateDays());
        Assert.Equal(TimeSpan.FromDays(22) + TimeSpan.FromHours(1), task.GetCompletionDuration());

        // 完成后不再算逾期 / 未完成
        Assert.Equal(0, task.GetOverdueDays(new DateOnly(2026, 4, 30)));
        Assert.Equal(0, task.GetPendingDays(new DateOnly(2026, 4, 30)));
    }
}
