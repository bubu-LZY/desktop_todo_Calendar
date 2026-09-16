using System.Collections.ObjectModel;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.ViewModels;

public sealed class DayCellViewModel : ViewModelBase
{
    /// <summary>快速添加时默认的任务时刻：当天 9:00，与 <see cref="CalendarTask.DefaultTime"/> 一致。</summary>
    private static readonly TimeSpan DefaultDraftTime = CalendarTask.DefaultTime.ToTimeSpan();

    private bool _isAddingTask;
    private string _draftTitle = string.Empty;
    private TimeSpan _draftTime = DefaultDraftTime;
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
        ReminderLeadLabels = [];
        ReminderLeadLabels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ReminderLeadSummary));
        RenumberTasks();
    }

    public DateOnly Date { get; }
    public bool IsInCurrentMonth { get; }
    /// <summary>非本月日期（Avalonia 宿主的条件样式类用它挑底色）。</summary>
    public bool IsOutMonth => !IsInCurrentMonth;
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

    /// <summary>
    /// 格子里快速添加时选的任务时刻（日期右边那个时间选择器）。
    /// 清空选择就回到当天 9:00 —— 与"没选时间"是同一种含义。
    /// </summary>
    public TimeSpan? DraftTime
    {
        get => _draftTime;
        set
        {
            if (SetProperty(ref _draftTime, value ?? DefaultDraftTime))
            {
                OnPropertyChanged(nameof(DraftTimeOnly));
            }
        }
    }

    /// <summary>草稿任务时刻对应的 <see cref="TimeOnly"/>（落盘时用它）。</summary>
    public TimeOnly DraftTimeOnly => TimeOnly.FromTimeSpan(_draftTime);

    /// <summary>多选下拉里可勾选的提醒档位（不含「不提醒」；一个都不勾就是不提醒）。</summary>
    public IReadOnlyList<string> ReminderLeadOptions => Helpers.ReminderLeadCatalog.SelectableLabels;

    /// <summary>格子里快速添加时勾选的提醒档位标签集合（多选）。</summary>
    public ObservableCollection<string> ReminderLeadLabels { get; }

    /// <summary>下拉按钮上的摘要文本：空 = 「不提醒」，否则把勾选项顿号连起来。</summary>
    public string ReminderLeadSummary => Helpers.ReminderLeadCatalog.Summarize(ReminderLeadLabels);

    /// <summary>格子草稿对应的全部提前提醒量（分钟）；空集合 = 不提醒。</summary>
    public IReadOnlyList<int> DraftReminderLeads
        => Helpers.ReminderLeadCatalog.ToMinutesList(ReminderLeadLabels);

    public void BeginAdd()
    {
        ResetDraft();
        IsAddingTask = true;
    }

    public void CancelAdd()
    {
        ResetDraft();
        IsAddingTask = false;
    }

    /// <summary>把草稿恢复成"刚打开表单"的样子：内容空、时刻 9:00、一个提醒都不勾。</summary>
    private void ResetDraft()
    {
        DraftTitle = string.Empty;
        DraftTime = DefaultDraftTime;
        ReminderLeadLabels.Clear();
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
            RenumberTasks();
            return;
        }

        // 刷新会重建任务 ViewModel，需保留未提交的输入状态，
        // 否则用户正在输入的草稿 / 正在改的标题会被静默吞掉。
        var wasAdding = IsAddingTask;
        var draft = DraftTitle;
        var draftTime = DraftTime;
        var editing = Tasks.FirstOrDefault(task => task.IsEditing);
        var editingId = editing?.Id;
        var editingText = editing?.EditTitle;

        // 可复用实例的池子。用「列表 + 消费式匹配」而不是 Id 字典：
        // 字典遇到两条 Id 相同的任务（同步/导入都可能产生）会复用同一个实例两次，
        // 下面的增量插入会把其中一条吃掉，格子里就比右侧面板少显示一条。
        var reusable = new List<TaskItemViewModel>(Tasks);

        // 计算刷新后的目标顺序（复用已有实例，就地刷新属性）
        var desired = new List<TaskItemViewModel>(incoming.Count);
        foreach (var task in incoming)
        {
            var index = reusable.FindIndex(item => item.Id == task.Id);
            if (index >= 0)
            {
                // 同一实例：仅就地刷新完成/重要/标题，复选框元素得以保留
                var vm = reusable[index];
                reusable.RemoveAt(index);
                vm.SyncFromModel();
                desired.Add(vm);
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
            DraftTime = draftTime;
            IsAddingTask = true;
        }

        RefreshHolidays(incomingHolidays);
        RenumberTasks();
    }

    /// <summary>
    /// 重排本格任务的显示序号（1 起），让每一条任务前面都有「1.」「2.」。
    /// 用 VM 上的 OrderIndex 而不是 XAML 里的 AlternationIndex：Avalonia 不支持
    /// WPF 那个附加属性，而序号又必须跟着"重要优先 → 创建时间"的排序走。
    /// </summary>
    private void RenumberTasks()
    {
        for (var i = 0; i < Tasks.Count; i++)
        {
            Tasks[i].OrderIndex = i + 1;
        }
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
