using System.Collections.ObjectModel;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.ViewModels;

public sealed class DayCellViewModel : ViewModelBase
{
    private bool _isAddingTask;
    private string _draftTitle = string.Empty;
    private bool _isSelected;

    public DayCellViewModel(
        DateOnly date,
        bool isInCurrentMonth,
        bool isToday,
        IEnumerable<TaskItemViewModel> tasks,
        IEnumerable<ChinaHoliday> holidays)
    {
        Date = date;
        IsInCurrentMonth = isInCurrentMonth;
        IsToday = isToday;
        Tasks = new ObservableCollection<TaskItemViewModel>(tasks);
        Holidays = new ObservableCollection<ChinaHoliday>(holidays);
    }

    public DateOnly Date { get; }
    public bool IsInCurrentMonth { get; }
    public bool IsToday { get; }
    public ObservableCollection<TaskItemViewModel> Tasks { get; }
    public ObservableCollection<ChinaHoliday> Holidays { get; }
    public string DayNumber => Date.Day.ToString();
    public string Header => $"{Date:MM月dd日}";
    public int CompletedCount => Tasks.Count(task => task.IsCompleted);
    public int OpenCount => Tasks.Count(task => !task.IsCompleted);
    public bool HasHolidays => Holidays.Count > 0;
    public string FirstHolidayBadge => Holidays.FirstOrDefault()?.YearBadgeText ?? string.Empty;

    /// <summary>任务总数量</summary>
    public int TaskCount => Tasks.Count;

    /// <summary>是否有任务</summary>
    public bool HasTasks => Tasks.Count > 0;

    public bool IsAddingTask
    {
        get => _isAddingTask;
        set => SetProperty(ref _isAddingTask, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DraftTitle
    {
        get => _draftTitle;
        set => SetProperty(ref _draftTitle, value);
    }

    public void BeginAdd()
    {
        DraftTitle = string.Empty;
        IsAddingTask = true;
    }

    public void CancelAdd()
    {
        DraftTitle = string.Empty;
        IsAddingTask = false;
    }

    /// <summary>
    /// 在不重建整个月份结构的前提下，原地刷新本格的任务与节假日（用于时间轴增量更新）。
    ///
    /// 关键：尽量复用已有的 <see cref="TaskItemViewModel"/> 实例（按 Id 对应），
    /// 只对集合做"增/删/移动"，绝不 Clear 后整体重建。
    /// 否则勾选完成时整棵子视觉树被销毁重建：复选框元素被换掉会抢走焦点，
    /// 触发输入法在中/英之间反复切换，且界面会明显"闪一下"。
    ///
    /// ⚠️ 无论走哪条路径，都必须对复用的 VM 调 SyncFromModel()。
    /// 底层 CalendarTask 是 POCO、不发通知，而格子里这些 VM 与右侧面板里的
    /// VM 是不同的实例 —— 右侧勾选完成后，只有这里显式同步，格子才会跟着变。
    /// （历史上把这一步删掉过一次，直接导致"右侧勾完，月份小格子不更新"。）
    /// </summary>
    public void Refresh(
        IEnumerable<CalendarTask>? allTasks,
        IEnumerable<ChinaHoliday>? holidays)
    {
        var incoming = allTasks as IReadOnlyList<CalendarTask> ?? allTasks?.ToList() ?? new List<CalendarTask>();
        var incomingHolidays = holidays ?? Enumerable.Empty<ChinaHoliday>();

        // 集合结构（成员 / 顺序）没变：只要把每个 VM 的状态同步一遍即可，
        // 不动 ObservableCollection，容器与复选框元素得以完整保留。
        if (!TaskListsDiffer(incoming))
        {
            foreach (var existing in Tasks)
            {
                existing.SyncFromModel();
            }

            RefreshHolidays(incomingHolidays);
            return;
        }

        // 刷新会重建任务 ViewModel，需保留未提交的输入状态，
        // 否则用户正在输入的草稿 / 正在改的标题会被静默吞掉。
        var wasAdding = IsAddingTask;
        var draft = DraftTitle;
        var editing = Tasks.FirstOrDefault(task => task.IsEditing);
        var editingId = editing?.Id;
        var editingText = editing?.EditTitle;

        // 建立 Id -> 现有实例 的映射，优先复用，避免销毁复选框元素。
        var existingById = new Dictionary<Guid, TaskItemViewModel>(Tasks.Count);
        foreach (var item in Tasks)
        {
            existingById[item.Id] = item;
        }

        // 计算刷新后的目标顺序（复用已有实例，就地刷新属性）
        var desired = new List<TaskItemViewModel>(incoming.Count);
        foreach (var task in incoming)
        {
            if (existingById.TryGetValue(task.Id, out var vm))
            {
                // 同一实例：仅就地刷新完成/重要/标题，复选框元素得以保留
                vm.SyncFromModel();
                desired.Add(vm);
                existingById.Remove(task.Id);
            }
            else
            {
                var newVm = new TaskItemViewModel(task);
                if (editingId == task.Id && editingText is not null)
                {
                    newVm.EditTitle = editingText;
                    newVm.IsEditing = true;
                }

                desired.Add(newVm);
            }
        }

        // 用最小变更把 Tasks 同步成 desired：
        // 1) 先删掉不再存在的项；
        // 2) 再按目标顺序插入/移动，复用同一实例 => 容器不重建、焦点不丢。
        for (var i = Tasks.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Tasks[i]))
            {
                Tasks.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var vm = desired[i];
            var current = Tasks.IndexOf(vm);
            if (current == -1)
            {
                Tasks.Insert(i, vm);
            }
            else if (current != i)
            {
                Tasks.Move(current, i);
            }
        }

        if (wasAdding)
        {
            DraftTitle = draft;
            IsAddingTask = true;
        }

        RefreshHolidays(incomingHolidays);
    }

    /// <summary>
    /// 只重推与时间相关的派生文本（"未完成 N 天" / 悬浮提示）。
    /// 时钟 tick 或跨天时调用，让悬浮信息跟着当前时间走。
    /// </summary>
    public void RefreshDerivedText()
    {
        foreach (var task in Tasks)
        {
            task.RefreshDerived();
        }
    }

    /// <summary>
    /// 比对传入任务与"UI 当前显示的状态"是否有实质差异（数量、顺序、id、标题、完成、重要）。
    ///
    /// ⚠️ 必须拿 VM 的快照（Shown*）比，不能拿 VM 的 IsCompleted / Title 比：
    /// 那些属性是 model 的直通读取口，model 一改两边同时变，diff 恒为 false，
    /// 于是"改重要标记导致顺序要变"这类结构变更永远检测不到。
    /// </summary>
    private bool TaskListsDiffer(IReadOnlyList<CalendarTask> incoming)
    {
        if (Tasks.Count != incoming.Count)
        {
            return true;
        }

        for (var i = 0; i < incoming.Count; i++)
        {
            var existing = Tasks[i];
            var next = incoming[i];
            if (existing.Id != next.Id
                || existing.ShownIsCompleted != next.IsCompleted
                || existing.ShownIsImportant != next.IsImportant
                || !string.Equals(existing.ShownTitle, next.Title, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshHolidays(IEnumerable<ChinaHoliday> holidays)
    {
        var incoming = holidays as IReadOnlyList<ChinaHoliday> ?? holidays.ToList();
        var differ = Holidays.Count != incoming.Count;
        if (!differ)
        {
            for (var i = 0; i < incoming.Count; i++)
            {
                if (!ReferenceEquals(Holidays[i], incoming[i]))
                {
                    differ = true;
                    break;
                }
            }
        }

        if (differ)
        {
            Holidays.Clear();
            foreach (var holiday in incoming)
            {
                Holidays.Add(holiday);
            }
        }

        OnPropertyChanged(nameof(TaskCount));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(HasHolidays));
        OnPropertyChanged(nameof(FirstHolidayBadge));
    }
}
