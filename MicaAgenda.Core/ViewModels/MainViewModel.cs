using System.Collections.ObjectModel;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    /// <summary>时间轴最多保留的月份块数量（约 1 年），超出后从远端裁剪。</summary>
    public const int TimelineMaxMonths = 12;

    /// <summary>
    /// 启动时以锚点月为中心向前后各构建几个月。
    /// 之前是 2（共 5 块 / 210 个日期格子），首屏要一次性建完整棵视觉树，是"启动卡两秒"的主因；
    /// 改成 1（共 3 块 / 126 格）后首屏明显更快，滚到边缘时由 ExtendTimeline 自动补建。
    /// </summary>
    public const int TimelineInitialSpan = 1;

    private readonly CalendarData _data;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly object _syncRoot;
    private List<ChinaHoliday> _holidays;
    private DateOnly _selectedDate;
    private DateOnly _today;
    private DateOnly _timelineAnchor;
    private DateOnly? _selectedCellDate;
    private DateOnly _panelDate;
    private bool _isAddingTodayTask;
    private string _todayTaskDraft = string.Empty;
    private string _todayTaskLead = ReminderNoneLabel;
    private TimeSpan? _todayTaskTime = CalendarTask.DefaultTime.ToTimeSpan();

    /// <summary>本周三个分组共用的 VM 实例池（按任务 Id），保证跨组移动时实例不销毁。</summary>
    private readonly Dictionary<Guid, TaskItemViewModel> _weekVmPool = new();

    /// <summary>上一次全量重推派生文本的时刻，用于节流。</summary>
    private DateTimeOffset _lastDerivedTextRefresh = DateTimeOffset.MinValue;

    public MainViewModel(
        CalendarData data,
        Func<DateTimeOffset>? nowProvider = null,
        IEnumerable<ChinaHoliday>? holidays = null,
        object? syncRoot = null)
    {
        _data = data;
        _syncRoot = syncRoot ?? new object();
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        _holidays = holidays?.ToList() ?? [];
        _today = DateOnly.FromDateTime(_nowProvider().LocalDateTime);
        _selectedDate = _today;
        _timelineAnchor = _today;
        _panelDate = _today;
        VisibleDays = [];
        YearMonths = [];
        TodayTasks = [];
        SetYearCommand = new RelayCommand(_ => SetViewMode(CalendarViewMode.Year));
        SetMonthCommand = new RelayCommand(_ => SetViewMode(CalendarViewMode.Month));
        SetWeekCommand = new RelayCommand(_ => SetViewMode(CalendarViewMode.Week));
        PreviousCommand = new RelayCommand(_ => MovePrevious());
        NextCommand = new RelayCommand(_ => MoveNext());
        TodayCommand = new RelayCommand(_ => GoToday());
        BuildTimeline();
        RebuildCalendar();
    }

    public CalendarData Data => _data;
    public CalendarSettings Settings => _data.Settings;
    public ObservableCollection<DayCellViewModel> VisibleDays { get; }
    public ObservableCollection<MonthSummaryViewModel> YearMonths { get; }

    /// <summary>
    /// 三个月/周/年视图容器的可见性，给 XAML 直接绑。
    ///
    /// 以前 XAML 绑的是 <c>Settings.ViewMode</c> 这种「嵌套属性路径」，而中间那层
    /// <see cref="Models.CalendarSettings"/> 是纯 POCO（没实现 INotifyPropertyChanged），
    /// 视图模式改了这一层路径刷不动 —— 表现就是点了「周视图 / 年视图」画面纹丝不动、
    /// 一直停在月视图上。改成绑单层布尔值，通知一定送得到。
    /// </summary>
    public bool IsMonthView => Settings.ViewMode == CalendarViewMode.Month;
    public bool IsWeekView => Settings.ViewMode == CalendarViewMode.Week;
    public bool IsYearView => Settings.ViewMode == CalendarViewMode.Year;

    /// <summary>
    /// 月 / 年视图共用的那一套「左侧日历 + 右侧任务面板」布局是否在显示。
    /// 周视图不一样：它的任务面板在日期格子**下面**、占满剩余高度（老版本就是这个排布），
    /// 所以周视图下这套布局要整块收起，免得右侧面板从周视图底下透出来。
    /// </summary>
    public bool IsMonthOrYearView => Settings.ViewMode != CalendarViewMode.Week;

    /// <summary>今日任务面板数据源（独立于日历格子中的 ViewModel 实例，互不干扰）。</summary>
    public ObservableCollection<TaskItemViewModel> TodayTasks { get; }

    /// <summary>
    /// 本周（今天所在的那一周）未完成且未到期的任务。
    /// 始终基于"今天"所在的周，**不随选中的日期变化**（与右侧"本周任务完成情况"保持不变一致）。
    /// </summary>
    public ObservableCollection<TaskItemViewModel> WeekOpenTasks { get; } = new();

    /// <summary>所有"日期 &lt; 今天 且 未完成"的任务（涵盖历史欠账），用于"逾期未完成"组。</summary>
    public ObservableCollection<TaskItemViewModel> WeekOverdueTasks { get; } = new();

    /// <summary>本周（今天所在的那一周）已完成的任务。</summary>
    public ObservableCollection<TaskItemViewModel> WeekCompletedTasks { get; } = new();

    /// <summary>当前被选中的日期格子（提升到 ViewModel 层，重建后仍能恢复高亮）。</summary>
    public DateOnly? SelectedCellDate => _selectedCellDate;

    /// <summary>连续多月份时间轴（月视图右侧滚动浏览用）。</summary>
    public ObservableCollection<MonthBlockViewModel> TimelineMonths { get; } = new();

    /// <summary>当前时间轴锚定到的月份，供视图滚动定位。</summary>
    public DateOnly TimelineAnchor => _timelineAnchor;

    /// <summary>时间轴结构被重建（如切换月份/点今天）时触发，视图应滚动定位到锚点月。</summary>
    public event Action? TimelineRebuilt;

    /// <summary>数据自上次保存以来是否有变更，用于按需落盘（避免周期性无谓写入）。</summary>
    public bool IsDirty { get; private set; }

    public void MarkDirty() => IsDirty = true;
    public void MarkSaved() => IsDirty = false;
    public RelayCommand SetYearCommand { get; }
    public RelayCommand SetMonthCommand { get; }
    public RelayCommand SetWeekCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand TodayCommand { get; }

    public DateOnly SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value))
            {
                RebuildCalendar();
            }
        }
    }

    public DateOnly Today
    {
        get => _today;
        private set => SetProperty(ref _today, value);
    }

    public string Title => Settings.ViewMode switch
    {
        CalendarViewMode.Year => $"{SelectedDate.Year}年",
        CalendarViewMode.Week => $"{GetWeekStart(SelectedDate):MM月dd日} - {GetWeekStart(SelectedDate).AddDays(6):MM月dd日}",
        _ => $"{SelectedDate:yyyy年M月}"
    };

    public string ClockText => _nowProvider().LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>今日日期星期显示（如："8月25日 周二"）</summary>
    public string TodayDisplay
    {
        get
        {
            var now = _nowProvider().LocalDateTime;
            var dayOfWeek = now.DayOfWeek switch
            {
                DayOfWeek.Sunday => "周日",
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => ""
            };
            return $"{now:M月d日} {dayOfWeek}";
        }
    }

    /// <summary>今日任务数量显示</summary>
    public string TodayTaskCount
    {
        get
        {
            var today = _today;
            lock (_syncRoot)
            {
                var count = _data.Tasks.Count(t => t.Date == today);
                return $"今日: {count}项";
            }
        }
    }

    /// <summary>本周任务数量显示</summary>
    public string ThisWeekTaskCount
    {
        get
        {
            var today = _today;
            var weekStart = GetWeekStart(today);
            var weekEnd = weekStart.AddDays(6);
            lock (_syncRoot)
            {
                var count = _data.Tasks.Count(t => t.Date >= weekStart && t.Date <= weekEnd);
                return $"本周: {count}项";
            }
        }
    }

    // ===== 今日任务面板 =====

    /// <summary>
    /// 面板当前展示的日期：默认跟随今日；在日历上选中某一天的格子后，
    /// 面板切换到展示该日的任务清单。清除选中后回到今日。
    /// </summary>
    public DateOnly PanelDate => _panelDate;

    /// <summary>面板标题（选中了非今日日期时展示对应日期）。</summary>
    public string PanelHeader => _panelDate == _today ? "今日任务" : $"{_panelDate:M月d日}任务";

    /// <summary>今日任务面板是否为空。</summary>
    public bool HasTodayTasks => TodayTasks.Count > 0;

    /// <summary>今日任务总数。</summary>
    public int TodayTaskTotal => TodayTasks.Count;

    /// <summary>今日未完成任务数。</summary>
    public int TodayTaskPending => TodayOpenTasks.Count;

    /// <summary>今日任务汇总文案（如 "共 5 项 · 待办 3"）。</summary>
    public string TodayTaskSummary => TodayTasks.Count == 0
        ? "暂无任务"
        : $"共 {TodayTasks.Count} 项 · 待办 {TodayTaskPending}";

    // ===== 面板的「未完成 / 已完成」两个子集合 =====
    //
    // 这里没有沿用 CollectionViewSource + PropertyGroupDescription 的分组方案：
    // PropertyGroupDescription 走的是 TypeDescriptor 反射 + 集合变更触发重排，
    // 对"同一批 item 里某个属性变了要换组"的支持非常脆弱 —— 实测勾选完成后任务
    // 不会从「未完成」组挪到「已完成」组（正是用户反馈的"取消已完成不会放回去"）。
    // 改成两个显式的 ObservableCollection，由 RefreshTodayTasks 直接搬运，
    // 行为完全可控、可预测。

    /// <summary>面板日期里尚未完成的任务。</summary>
    public ObservableCollection<TaskItemViewModel> TodayOpenTasks { get; } = new();

    /// <summary>面板日期里已完成的任务。</summary>
    public ObservableCollection<TaskItemViewModel> TodayCompletedTasks { get; } = new();

    public bool HasTodayOpen => TodayOpenTasks.Count > 0;
    public bool HasTodayCompleted => TodayCompletedTasks.Count > 0;

    // ===== 本周任务完成情况（始终基于今天所在的那一周） =====

    /// <summary>本周未完成任务数（本周内、未完成、未到截止日）。</summary>
    public int WeekOpenCount => WeekOpenTasks.Count;

    /// <summary>本周逾期未完成任务数（所有未完成且日期 &lt; 今天，覆盖历史欠账）。</summary>
    public int WeekOverdueCount => WeekOverdueTasks.Count;

    /// <summary>本周已完成任务数。</summary>
    public int WeekCompletedCount => WeekCompletedTasks.Count;

    /// <summary>本周总任务数（三组合计）。</summary>
    public int WeekTotalCount => WeekOpenCount + WeekOverdueCount + WeekCompletedCount;

    public bool HasWeekOpen => WeekOpenCount > 0;
    public bool HasWeekOverdue => WeekOverdueCount > 0;
    public bool HasWeekCompleted => WeekCompletedCount > 0;

    /// <summary>
    /// 本周的日期范围显示（如 "8月30日 - 9月5日"）。
    /// 始终是"今天"所在的那一周，与选中的日期无关。
    /// </summary>
    public string WeekRange
    {
        get
        {
            var ws = GetWeekStart(Today);
            var we = ws.AddDays(6);
            return $"{ws:M月d日} - {we:M月d日}";
        }
    }

    /// <summary>
    /// 本周汇总文案，单独一行显示在"本周任务完成情况"标题下方（如 "8月30日 - 9月5日 · 共 12 项"）。
    /// </summary>
    public string WeekSummary => WeekTotalCount == 0
        ? WeekRange
        : $"{WeekRange} · 共 {WeekTotalCount} 项";

    /// <summary>今日任务面板的快速输入框是否展开。</summary>
    public bool IsAddingTodayTask
    {
        get => _isAddingTodayTask;
        private set => SetProperty(ref _isAddingTodayTask, value);
    }

    /// <summary>今日任务面板的输入草稿。</summary>
    public string TodayTaskDraft
    {
        get => _todayTaskDraft;
        set => SetProperty(ref _todayTaskDraft, value);
    }

    /// <summary>「提醒时间」下拉的「不提醒」项（与 <see cref="Helpers.ReminderLeadCatalog"/> 同一份）。</summary>
    public const string ReminderNoneLabel = Helpers.ReminderLeadCatalog.NoneLabel;

    /// <summary>「提醒时间」下拉的可选项（与日期格子内快速添加共用）。</summary>
    public IReadOnlyList<string> ReminderLeadOptions { get; } = Helpers.ReminderLeadCatalog.Labels;

    /// <summary>当前选中的提醒档位（默认「不提醒」）。</summary>
    public string TodayTaskLead
    {
        get => _todayTaskLead;
        set
        {
            var label = string.IsNullOrWhiteSpace(value) ? ReminderNoneLabel : value;
            if (SetProperty(ref _todayTaskLead, label))
            {
                OnPropertyChanged(nameof(TodayTaskReminderLead));
            }
        }
    }

    /// <summary>
    /// 提前提醒量（分钟）：选了具体档位就是该分钟数，停在「不提醒」返回 null（= 这条任务不推提醒）。
    /// </summary>
    public int? TodayTaskReminderLead => Helpers.ReminderLeadCatalog.ToMinutes(_todayTaskLead);

    /// <summary>
    /// 面板快速添加时选的任务时刻（默认当天 9:00）。
    /// 清空选择就回到 9:00 —— 「没选时间」和「就是 9 点」在这里是同一种含义。
    /// </summary>
    public TimeSpan? TodayTaskTime
    {
        get => _todayTaskTime;
        set
        {
            if (SetProperty(ref _todayTaskTime, value ?? CalendarTask.DefaultTime.ToTimeSpan()))
            {
                OnPropertyChanged(nameof(TodayTaskTimeOnly));
            }
        }
    }

    /// <summary>草稿任务时刻对应的 <see cref="TimeOnly"/>（落盘时用它）。</summary>
    public TimeOnly TodayTaskTimeOnly => TimeOnly.FromTimeSpan(_todayTaskTime ?? CalendarTask.DefaultTime.ToTimeSpan());

    /// <summary>展开今日任务的快速输入框。</summary>
    public void BeginAddTodayTask()
    {
        TodayTaskDraft = string.Empty;
        ResetLeadLabel();
        IsAddingTodayTask = true;
    }

    /// <summary>收起今日任务的快速输入框。</summary>
    public void CancelTodayTask()
    {
        TodayTaskDraft = string.Empty;
        ResetLeadLabel();
        IsAddingTodayTask = false;
    }

    /// <summary>
    /// 提交今日任务的快速输入，返回新建的任务（草稿为空则返回 null）。
    /// 任务的时刻由表单里选（默认当天 9:00），提前提醒量相对它往前推。
    /// </summary>
    public CalendarTask? CommitTodayTask()
    {
        var title = TodayTaskDraft.Trim();
        var lead = TodayTaskReminderLead;
        var time = TodayTaskTimeOnly;

        TodayTaskDraft = string.Empty;
        ResetLeadLabel();
        IsAddingTodayTask = false;

        return string.IsNullOrWhiteSpace(title) ? null : AddTask(_panelDate, title, lead, time);
    }

    /// <summary>草稿恢复成"刚展开"的样子：提醒回到「不提醒」、任务时刻回到当天 9:00。</summary>
    private void ResetLeadLabel()
    {
        TodayTaskLead = ReminderNoneLabel;
        TodayTaskTime = CalendarTask.DefaultTime.ToTimeSpan();
    }

    /// <summary>切换面板展示的日期并刷新任务清单。</summary>
    private void SetPanelDate(DateOnly date)
    {
        if (_panelDate == date)
        {
            return;
        }

        _panelDate = date;
        RefreshTodayTasks();
        OnPropertyChanged(nameof(PanelDate));
        OnPropertyChanged(nameof(PanelHeader));
    }

    /// <summary>
    /// 刷新今日任务面板数据（保留正在编辑的条目）。
    /// 面板展示日期为面板日期（PanelDate）：默认是今日，选中某个日期格子后为该日期。
    ///
    /// TodayTasks 是这一天的任务总池（按 重要优先 → 创建时间 排序），
    /// TodayOpenTasks / TodayCompletedTasks 引用的是同一批 VM 实例的两个视图。
    /// 勾选完成时 VM 会从一个集合挪到另一个集合 —— 实例本身不销毁，
    /// 所以复选框、输入焦点、编辑草稿都不会丢。
    /// </summary>
    public void RefreshTodayTasks()
    {
        List<CalendarTask> tasks;
        lock (_syncRoot)
        {
            tasks = _data.Tasks
                .Where(task => task.Date == _panelDate)
                .OrderByDescending(task => task.IsImportant)
                .ThenBy(task => task.CreatedAt)
                .ToList();
        }

        // 复用已有 TaskItemViewModel 实例（按 Id 对应），仅做增/删/移动，
        // 不整体 Clear 重建——否则勾选完成时复选框元素被销毁、焦点丢失，
        // 输入法会在中/英之间反复切换，界面也会明显闪动。
        var editing = TodayTasks.FirstOrDefault(task => task.IsEditing);
        var editingId = editing?.Id;
        var editingText = editing?.EditTitle;

        var existingById = new Dictionary<Guid, TaskItemViewModel>(TodayTasks.Count);
        foreach (var item in TodayTasks)
        {
            existingById[item.Id] = item;
        }

        var desired = new List<TaskItemViewModel>(tasks.Count);
        foreach (var task in tasks)
        {
            if (existingById.TryGetValue(task.Id, out var vm))
            {
                vm.SyncFromModel();
                desired.Add(vm);
                existingById.Remove(task.Id);
            }
            else
            {
                var newVm = new TaskItemViewModel(task, _nowProvider);
                if (editingId == task.Id && editingText is not null)
                {
                    newVm.EditTitle = editingText;
                    newVm.IsEditing = true;
                }

                desired.Add(newVm);
            }
        }

        Reconcile(TodayTasks, desired);

        // 两个分组各取 desired 的一个切片。顺序沿用总池顺序，视觉上保持稳定。
        var open = desired.Where(vm => !vm.IsCompleted).ToList();
        var completed = desired.Where(vm => vm.IsCompleted).ToList();
        Reconcile(TodayOpenTasks, open);
        Reconcile(TodayCompletedTasks, completed);

        OnPropertyChanged(nameof(HasTodayTasks));
        OnPropertyChanged(nameof(TodayTaskTotal));
        OnPropertyChanged(nameof(TodayTaskPending));
        OnPropertyChanged(nameof(TodayTaskSummary));
        OnPropertyChanged(nameof(HasTodayOpen));
        OnPropertyChanged(nameof(HasTodayCompleted));
    }

    /// <summary>
    /// 把 target 集合最小变更地同步成 desired：只做必要的 增/删/移动，
    /// 复用同一批 VM 实例，让 WPF 保留现有容器（复选框、焦点、输入草稿）。
    /// </summary>
    private static void Reconcile(
        ObservableCollection<TaskItemViewModel> target,
        IReadOnlyList<TaskItemViewModel> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var vm = desired[i];
            var current = target.IndexOf(vm);
            if (current == -1)
            {
                var index = Math.Min(i, target.Count);
                target.Insert(index, vm);
            }
            else if (current != i)
            {
                target.Move(current, i);
            }
        }
    }

    /// <summary>
    /// 删除当前面板日期（_panelDate）的所有任务——用于"今日任务"区或选中的某一天任务区一键清空。
    /// 走完整的 RebuildCalendar：月视图下它会增量刷新时间轴上的每个格子，
    /// 保证被删掉的任务也从左侧日历小格子里消失（只刷 TodayTasks 会留下残影）。
    /// </summary>
    public int ClearPanelDateTasks()
    {
        List<CalendarTask> toRemove;
        lock (_syncRoot)
        {
            toRemove = _data.Tasks
                .Where(t => t.Date == _panelDate)
                .ToList();
            foreach (var t in toRemove)
            {
                _data.Tasks.Remove(t);
            }
        }

        MarkDirty();
        RebuildCalendar();

        // 一键清空同样要通知对端：漏掉的话，清掉的复习任务下次同步会被重建，表现为「删不掉」。
        NotifyEach(ReviewTaskDeleted, toRemove);
        return toRemove.Count;
    }

    /// <summary>
    /// 删除数据库里的所有任务，相当于「重置为初始状态」。
    /// 调用方需要做二次确认（UI 已经做了 MessageBox 拦截）。
    /// 返回删除的任务数。
    /// </summary>
    public int DeleteAllTasks()
    {
        List<CalendarTask> removedReviewTasks;
        int removed;
        lock (_syncRoot)
        {
            removed = _data.Tasks.Count;
            removedReviewTasks = _data.Tasks.Where(t => t.IsReviewTask).ToList();
            _data.Tasks.Clear();
        }

        RebuildCalendar();
        MarkDirty();

        // 连同复习任务一起通知对端，否则下一次同步会把它们全部重建回来。
        NotifyEach(ReviewTaskDeleted, removedReviewTasks);
        return removed;
    }

    /// <summary>
    /// 批量改动后的对端通知：只挑复习任务，逐个抛事件。
    /// 放在锁外做——接收方会去发网络请求，持锁调用会把界面卡住。
    /// </summary>
    private static void NotifyEach(Action<CalendarTask>? handler, IEnumerable<CalendarTask> tasks)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var task in tasks.Where(t => t.IsReviewTask).ToList())
        {
            handler(task);
        }
    }
    /// <summary>
    /// 刷新"本周任务完成情况"的三组集合（未完成 / 逾期未完成 / 已完成）。
    /// 始终基于今天所在的那一周 + 所有未完成的过期任务，与选中的日期无关。
    ///
    /// 复用 TaskItemViewModel 实例（按 Id 对应），与 RefreshTodayTasks 相同的最小变更原则：
    /// 避免 Clear+重建整棵子视觉树导致复选框销毁、输入法切换、界面闪动。
    /// </summary>

    /// <summary>
    /// 清空"逾期未完成"组的全部任务。返回删除数。
    /// 与 <see cref="RefreshWeekTasks"/> 同一个口径（过了任务时刻就算逾期），
    /// 否则按钮删掉的集合会跟界面上显示的那一组对不上。
    /// </summary>
    public int ClearOverdueTasks() => BulkRemove(t => IsOverdueTask(t, _nowProvider().LocalDateTime));

    /// <summary>清空"未完成"组的全部任务（只删本周内还没到点的那些）。返回删除数。</summary>
    public int ClearOpenTasks()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);
        var nowLocal = _nowProvider().LocalDateTime;
        return BulkRemove(t => IsOpenTask(t, nowLocal, weekStart, weekEnd));
    }

    /// <summary>清空"已完成"组的全部任务（本周内已完成的所有任务）。返回删除数。</summary>
    public int ClearCompletedTasks()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);
        return BulkRemove(t => t.IsCompleted && t.Date >= weekStart && t.Date <= weekEnd);
    }

    /// <summary>把"逾期未完成"组的全部任务标记为已完成。返回处理数。</summary>
    public int MarkOverdueCompleted()
    {
        var now = _nowProvider();
        return BulkUpdate(t => IsOverdueTask(t, now.LocalDateTime), t => t.MarkCompleted(now));
    }

    /// <summary>把"未完成"组的全部任务标记为已完成。返回处理数。</summary>
    public int MarkOpenCompleted()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);
        var now = _nowProvider();
        return BulkUpdate(
            t => IsOpenTask(t, now.LocalDateTime, weekStart, weekEnd),
            t => t.MarkCompleted(now));
    }

    /// <summary>把"已完成"组的全部任务标记为未完成。返回处理数。</summary>
    public int MarkCompletedIncomplete()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);
        return BulkUpdate(
            t => t.IsCompleted && t.Date >= weekStart && t.Date <= weekEnd,
            t => t.MarkIncomplete());
    }

    private int BulkRemove(Func<CalendarTask, bool> predicate)
    {
        List<CalendarTask> toRemove;
        lock (_syncRoot)
        {
            toRemove = _data.Tasks.Where(predicate).ToList();
            foreach (var t in toRemove)
            {
                _data.Tasks.Remove(t);
            }
        }

        RebuildCalendar();
        MarkDirty();

        // 批量删除同样要通知对端。「逾期未完成 / 未完成 / 已完成」三组里都可能混着复习任务，
        // 漏掉的话它们会在下一次同步时被按对端计划重建，用户看到的就是「删了又回来」。
        NotifyEach(ReviewTaskDeleted, toRemove);
        return toRemove.Count;
    }

    private int BulkUpdate(Func<CalendarTask, bool> predicate, Action<CalendarTask> action)
    {
        int count;
        List<CalendarTask> statusChangedReviewTasks = [];
        lock (_syncRoot)
        {
            var hits = _data.Tasks.Where(predicate).ToList();
            foreach (var t in hits)
            {
                var wasCompleted = t.IsCompleted;
                action(t);
                if (t.IsCompleted != wasCompleted && t.IsReviewTask)
                {
                    statusChangedReviewTasks.Add(t);
                }
            }
            count = hits.Count;
        }

        RebuildCalendar();
        MarkDirty();

        // 批量改完成态也要立刻回推：对端按「状态最后变更时间」仲裁，只等每小时一次的定时同步太慢。
        NotifyEach(ReviewTaskStatusChanged, statusChangedReviewTasks);
        return count;
    }

    public void RefreshWeekTasks()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);

        // 逾期与否看"任务时刻"（日期 + 时间，没设时间按当天 9:00）：
        // 今天 9:00 的任务到 10:00 还没勾就算逾期，今天 23:00 的还留在「未完成」。
        var nowLocal = _nowProvider().LocalDateTime;

        List<CalendarTask> open;
        List<CalendarTask> overdue;
        List<CalendarTask> completed;
        lock (_syncRoot)
        {
            open = _data.Tasks
                .Where(t => IsOpenTask(t, nowLocal, weekStart, weekEnd))
                .OrderBy(t => t.Date)
                .ThenBy(t => t.CreatedAt)
                .ToList();
            overdue = _data.Tasks
                .Where(t => IsOverdueTask(t, nowLocal))
                .OrderBy(t => t.Date)
                .ThenBy(t => t.CreatedAt)
                .ToList();

            // 判定用「计划日期在本周」或「实际完成于本周」的并集：
            // 只用 Date 判定的话，一条 8 月的逾期任务在今天勾完就会从三组里同时消失
            // （既不再是"逾期未完成"，也不算"本周完成"），看起来像任务凭空丢了。
            completed = _data.Tasks
                .Where(t => t.IsCompleted && IsInWeek(t, weekStart, weekEnd))
                .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
                .ThenBy(t => t.CreatedAt)
                .ToList();
        }

        var openVms = open.Select(GetWeekVm).ToList();
        var overdueVms = overdue.Select(GetWeekVm).ToList();
        var completedVms = completed.Select(GetWeekVm).ToList();

        Reconcile(WeekOpenTasks, openVms);
        Reconcile(WeekOverdueTasks, overdueVms);
        Reconcile(WeekCompletedTasks, completedVms);

        PruneWeekVmPool(openVms, overdueVms, completedVms);

        OnPropertyChanged(nameof(WeekOpenCount));
        OnPropertyChanged(nameof(WeekOverdueCount));
        OnPropertyChanged(nameof(WeekCompletedCount));
        OnPropertyChanged(nameof(WeekTotalCount));
        OnPropertyChanged(nameof(HasWeekOpen));
        OnPropertyChanged(nameof(HasWeekOverdue));
        OnPropertyChanged(nameof(HasWeekCompleted));
        OnPropertyChanged(nameof(WeekRange));
        OnPropertyChanged(nameof(WeekSummary));
    }

    /// <summary>
    /// 「逾期未完成」：未完成，且已经过了任务时刻（日期 + 时间，没设时间按当天 9:00）。
    /// 右侧三组、清空/批量完成的按钮都走这一个口径，界面显示的与按钮作用到的才是同一批任务。
    /// </summary>
    private static bool IsOverdueTask(CalendarTask task, DateTime nowLocal) => task.IsOverdueAt(nowLocal);

    /// <summary>
    /// 「未完成」：落在本周窗口内、未完成、而且还没到任务时刻。
    /// </summary>
    private static bool IsOpenTask(CalendarTask task, DateTime nowLocal, DateOnly weekStart, DateOnly weekEnd)
        => !task.IsCompleted
           && !IsOverdueTask(task, nowLocal)
           && task.Date >= weekStart
           && task.Date <= weekEnd;

    /// <summary>任务是否落在本周：计划日期在本周，或实际完成时刻在本周。</summary>
    private static bool IsInWeek(CalendarTask task, DateOnly weekStart, DateOnly weekEnd)
    {
        if (task.Date >= weekStart && task.Date <= weekEnd)
        {
            return true;
        }

        // 没有 CompletedAt 的旧数据（或经 API 写入的）只按计划日期判定
        return task.CompletedAt is { } at
               && DateOnly.FromDateTime(at.LocalDateTime) >= weekStart
               && DateOnly.FromDateTime(at.LocalDateTime) <= weekEnd;
    }

    /// <summary>
    /// 取（或建）该任务在本周分组里复用的 VM 实例。
    /// 三个分组共用同一个池子，任务从「未完成」挪到「已完成」时带走的是同一个实例，
    /// 复选框容器与编辑草稿都不会被销毁。
    /// </summary>
    private TaskItemViewModel GetWeekVm(CalendarTask task)
    {
        if (_weekVmPool.TryGetValue(task.Id, out var vm))
        {
            vm.SyncFromModel();
            return vm;
        }

        vm = new TaskItemViewModel(task, _nowProvider);
        _weekVmPool[task.Id] = vm;
        return vm;
    }

    /// <summary>清掉池子里已经不再出现在任何分组中的 VM，避免长期运行后无限增长。</summary>
    private void PruneWeekVmPool(params IReadOnlyList<TaskItemViewModel>[] keep)
    {
        if (_weekVmPool.Count <= 64)
        {
            return;
        }

        var alive = new HashSet<Guid>();
        foreach (var list in keep)
        {
            foreach (var vm in list)
            {
                alive.Add(vm.Id);
            }
        }

        var dead = _weekVmPool.Keys.Where(id => !alive.Contains(id)).ToList();
        foreach (var id in dead)
        {
            _weekVmPool.Remove(id);
        }
    }

    // ===== 日期格子选中 =====

    /// <summary>选中指定日期的格子（状态存于 ViewModel，日历重建后自动恢复高亮），并同步面板到该日期。</summary>
    public void SelectCell(DateOnly date)
    {
        if (_selectedCellDate == date)
        {
            return;
        }

        ApplyCellSelection(_selectedCellDate, false);
        _selectedCellDate = date;
        ApplyCellSelection(date, true);
        SetPanelDate(date);
    }

    /// <summary>清除日期格子选中状态，面板回到今日。</summary>
    public void ClearCellSelection()
    {
        if (_selectedCellDate is not null)
        {
            ApplyCellSelection(_selectedCellDate, false);
            _selectedCellDate = null;
        }

        SetPanelDate(_today);
    }

    private void ApplyCellSelection(DateOnly? date, bool selected)
    {
        if (date is null)
        {
            return;
        }

        foreach (var cell in VisibleDays.Where(cell => cell.Date == date))
        {
            cell.IsSelected = selected;
        }

        if (Settings.ViewMode == CalendarViewMode.Year)
        {
            foreach (var month in YearMonths)
            {
                foreach (var cell in month.Days.Where(cell => cell.Date == date))
                {
                    cell.IsSelected = selected;
                }
            }

            return;
        }

        foreach (var block in TimelineMonths)
        {
            foreach (var cell in block.Days.Where(cell => cell.Date == date))
            {
                cell.IsSelected = selected;
            }
        }
    }

    public double MonthCellHeight => Math.Clamp(Settings.CellHeight, 48, 150);

    /// <summary>周视图格子高度：独立可调（拖右下角手柄时修改），不再跟着月视图的 CellHeight 走。</summary>
    public double WeekCellHeight => Math.Clamp(Settings.WeekCellHeight, 88, 280);

    /// <summary>
    /// 周视图底部面板（今日任务 + 本周任务完成情况）允许的最大高度。
    /// 之前是写死的 260，导致把周视图拉大时只有上面的日期格子变高、底部面板纹丝不动。
    /// 现在跟着 WeekCellHeight 一起放大，拉伸窗口时上下两部分会一起变大。
    /// </summary>
    public double WeekPanelMaxHeight => Math.Clamp(WeekCellHeight * 2.5, 300, 900);

    /// <summary>
    /// 拉伸周视图高度：改格子高度而不是窗口高度（窗口会按内容自适应）。
    /// 返回是否真的发生了变化。
    /// </summary>
    public bool ResizeWeekCellHeight(double delta)
    {
        var next = Math.Clamp(Settings.WeekCellHeight + delta, 88, 280);
        if (Math.Abs(next - Settings.WeekCellHeight) < 0.01)
        {
            return false;
        }

        Settings.WeekCellHeight = next;
        RefreshHeightProperties();
        return true;
    }
    public double YearCellHeight => Math.Clamp(Settings.CellHeight * 0.38, 22, 58);
    public double YearMonthHeight => Math.Clamp(Settings.CellHeight * 2.85, 150, 360);
    public double CurrentDayCellHeight => Settings.ViewMode == CalendarViewMode.Week ? WeekCellHeight : MonthCellHeight;

    public void RefreshClock()
    {
        var now = DateOnly.FromDateTime(_nowProvider().LocalDateTime);
        var previousToday = _today;
        var dayChanged = now != _today;
        Today = now;
        OnPropertyChanged(nameof(ClockText));

        // 面板未被锁定到某个选中日期（即面板还在展示"今日"）时，跨天自动跟随新的今日
        if (dayChanged && _panelDate == previousToday)
        {
            SetPanelDate(now);
        }

        // 有未提交的行内输入时跳过重建，避免吞掉草稿；跨天则必须刷新
        if (!dayChanged && HasPendingInlineInput)
        {
            return;
        }

        // 日历内容以「天」为粒度：任务、节假日、"今日"高亮、逾期天数在一天之内都不会变，
        // 所以没跨天时只更新时钟文本与派生文本，不重建格子。
        // 早期实现是每分钟无条件重建一次：月视图要遍历整条时间轴的每个格子并重绑任务，
        // 年视图更是一次性重建 12 个月共 365 个格子（还得清空再逐个 Add 进集合），
        // 每次都会引发一整轮 UI 刷新——而结果与上一分钟完全相同。
        if (dayChanged)
        {
            if (Settings.ViewMode == CalendarViewMode.Month)
            {
                RefreshTimelineTasks();
            }
            else
            {
                RebuildCalendar();
            }
        }

        // 让"未完成 N 天 / 逾期 N 天"这类与时间相关的文本跟着走。
        // 悬浮提示本身是在弹出时实时计算的，这里主要服务于行内徽标。
        RefreshDerivedText(force: dayChanged);
    }

    /// <summary>
    /// 重推所有任务上与时间相关的派生文本（悬浮提示、行内徽标）。
    /// 节流到 10 分钟一次：这些文本以「天」为粒度，没必要每分钟全量刷一遍。
    /// </summary>
    public void RefreshDerivedText(bool force = false)
    {
        var now = _nowProvider();
        if (!force && now - _lastDerivedTextRefresh < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastDerivedTextRefresh = now;

        foreach (var block in TimelineMonths)
        {
            foreach (var cell in block.Days)
            {
                cell.RefreshDerivedText();
            }
        }

        foreach (var cell in VisibleDays)
        {
            cell.RefreshDerivedText();
        }

        foreach (var month in YearMonths)
        {
            foreach (var cell in month.Days)
            {
                cell.RefreshDerivedText();
            }
        }

        foreach (var vm in TodayTasks)
        {
            vm.RefreshDerived();
        }

        foreach (var vm in _weekVmPool.Values)
        {
            vm.RefreshDerived();
        }
    }

    /// <param name="reminderLeadMinutes">
    /// 提前提醒量（分钟）；null = 不提醒，0 = 「到时提醒」（任务时刻那一刻推）。
    /// 以任务时刻为锚点：任务 15:00 + 提前 30 分钟＝14:30 推。
    /// </param>
    /// <param name="time">
    /// 任务时刻（几点几分）；null 或正好 09:00 都按"没选"处理（= 当天默认时刻 9:00）。
    /// </param>
    public CalendarTask AddTask(DateOnly date, string title, int? reminderLeadMinutes = null, TimeOnly? time = null)
    {
        lock (_syncRoot)
        {
            var task = new CalendarTask
            {
                Date = date,
                Title = title.Trim(),
                CreatedAt = _nowProvider(),
                Time = NormalizeTaskTime(time),
                // 0 是「到时提醒」，要如实存下来；只有负数（以及没选）才算「不提醒」。
                ReminderLeadMinutes = NormalizeReminderLead(reminderLeadMinutes)
            };
            _data.Tasks.Add(task);
            IsDirty = true;
            RebuildCalendar();
            return task;
        }
    }

    /// <summary>
    /// 落盘前的任务时刻归一化：没选、或正好是当天默认时刻（9:00）都记 null。
    /// 免得同一条任务因为"显式选了 9:00"和"没选"而存成两种形态，
    /// 也让与 my-mindmap agent 同步来的复习任务（那边没有时间）保持同一种写法。
    /// </summary>
    private static TimeOnly? NormalizeTaskTime(TimeOnly? time)
        => time is null || time == CalendarTask.DefaultTime ? null : time;

    /// <summary>
    /// 落盘前的提醒档位归一化：只有负数（没有意义的提前量）算「不提醒」存 null。
    /// 0 必须原样保留 —— 它是「到时提醒」，和 null（不提醒）是两种不同行为。
    /// </summary>
    private static int? NormalizeReminderLead(int? reminderLeadMinutes)
        => reminderLeadMinutes is < 0 ? null : reminderLeadMinutes;

    public void RenameTask(Guid taskId, string title)
    {
        lock (_syncRoot)
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return;
            }

            task.Title = title.Trim();
            IsDirty = true;
            RebuildCalendar();
        }
    }

    /// <summary>
    /// 编辑任务：标题 / 任务时刻 / 提醒档位一次改完。
    ///
    /// 时刻或档位真的变了就作废原来的一次性提醒标记（<see cref="CalendarTask.ResetReminder"/>）：
    /// 按旧时刻推过的提醒不能挡住新时刻 —— 否则「把 9 点改成 15 点」之后当天再也不会响。
    /// 只改标题不动提醒，避免顺手把已经推过的提醒又推一遍。
    /// </summary>
    /// <param name="title">新标题；空白串视为"不改标题"（编辑框被清空时不至于把任务名抹掉）。</param>
    /// <param name="time">新任务时刻；null / 09:00 按"没选"处理（= 当天 9:00）。</param>
    /// <param name="reminderLeadMinutes">新提醒档位；null = 不提醒，0 = 到时提醒。</param>
    /// <returns>真的改到东西了返回 true（用于决定要不要提示"没有变化"）。</returns>
    public bool UpdateTask(Guid taskId, string title, TimeOnly? time, int? reminderLeadMinutes)
    {
        lock (_syncRoot)
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return false;
            }

            var newTitle = title.Trim();
            var newTime = NormalizeTaskTime(time);
            var newLead = NormalizeReminderLead(reminderLeadMinutes);

            if (newTitle.Length > 0)
            {
                task.Title = newTitle;
            }

            var scheduleChanged = task.Time != newTime || task.ReminderLeadMinutes != newLead;
            task.Time = newTime;
            task.ReminderLeadMinutes = newLead;
            if (scheduleChanged)
            {
                task.ResetReminder();
            }

            IsDirty = true;
            RebuildCalendar();
            return true;
        }
    }

    public void ToggleTaskCompletion(Guid taskId)
    {
        lock (_syncRoot)
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return;
            }

            var wasCompleted = task.IsCompleted;
            if (task.IsCompleted)
            {
                task.MarkIncomplete();
            }
            else
            {
                task.MarkCompleted(_nowProvider());
            }

            System.Diagnostics.Debug.WriteLine($"[TASK_TOGGLE] id={taskId} title='{task.Title}' {wasCompleted}->{task.IsCompleted}");
            IsDirty = true;
            RebuildCalendar();
        }
    }

    public void ToggleTaskImportance(Guid taskId)
    {
        lock (_syncRoot)
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return;
            }

            task.IsImportant = !task.IsImportant;
            IsDirty = true;
            RebuildCalendar();
        }
    }

    /// <summary>
    /// 复习任务（标题带复习前缀）被删除时触发。宿主据此通知 my-mindmap agent 一起删掉对应的
    /// 复习周期 —— 不做的话下一次同步会按对端复习计划把它重新建回来，用户看到的是「删不掉」。
    /// 放在事件里而不是直接调服务：删除入口有界面、MCP、HTTP API 多处，收到事件的地方只需接一次。
    /// </summary>
    public event Action<CalendarTask>? ReviewTaskDeleted;

    /// <summary>
    /// 复习任务的完成状态发生变化时触发。宿主据此把新状态与「状态最后变更时间」立刻推给
    /// my-mindmap agent —— 对端是按时间戳仲裁的，早推一次就早对齐一次。
    /// 单条勾选走界面事件、批量「标记完成 / 还原未完成」（逾期 / 未完成 / 已完成三组）走这里，
    /// 两条路都覆盖到，状态才不会有「要等一小时」的空窗。
    /// </summary>
    public event Action<CalendarTask>? ReviewTaskStatusChanged;

    public void DeleteTask(Guid taskId)
    {
        CalendarTask? removedReviewTask = null;
        lock (_syncRoot)
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return;
            }

            _data.Tasks.Remove(task);
            IsDirty = true;
            RebuildCalendar();
            if (task.IsReviewTask)
            {
                removedReviewTask = task;
            }
        }

        // 锁外通知：宿主收到后会去发网络请求，不能在锁内做
        if (removedReviewTask is not null)
        {
            ReviewTaskDeleted?.Invoke(removedReviewTask);
        }
    }

    public void SetViewMode(CalendarViewMode viewMode)
    {
        if (Settings.ViewMode == viewMode)
        {
            return;
        }

        Settings.ViewMode = viewMode;
        OnPropertyChanged(nameof(Settings));

        // 视图容器的可见性绑的是下面这三个单层布尔值（见属性注释），
        // 换视图时必须一起通知，否则画面不会跟着切。
        OnPropertyChanged(nameof(IsMonthView));
        OnPropertyChanged(nameof(IsWeekView));
        OnPropertyChanged(nameof(IsYearView));
        OnPropertyChanged(nameof(IsMonthOrYearView));

        // 切换视图时清除选中状态，让右侧面板回到今日任务
        ClearCellSelection();

        if (viewMode == CalendarViewMode.Month)
        {
            _timelineAnchor = new DateOnly(SelectedDate.Year, SelectedDate.Month, 1);
            BuildTimeline();
            RefreshHeightProperties();
        }
        else
        {
            RebuildCalendar();
        }
    }

    public void MovePrevious()
    {
        SelectedDate = Settings.ViewMode switch
        {
            CalendarViewMode.Year => SelectedDate.AddYears(-1),
            CalendarViewMode.Week => SelectedDate.AddDays(-7),
            _ => SelectedDate.AddMonths(-1)
        };
    }

    public void MoveNext()
    {
        SelectedDate = Settings.ViewMode switch
        {
            CalendarViewMode.Year => SelectedDate.AddYears(1),
            CalendarViewMode.Week => SelectedDate.AddDays(7),
            _ => SelectedDate.AddMonths(1)
        };
    }

    /// <summary>
    /// 回到今天。
    ///
    /// 这里**不能**只写 <c>SelectedDate = Today</c>：月视图是一条可无限滚动的时间轴、
    /// 年视图是一整年 12 个月的滚动列表，"现在看的是哪一段"是由**滚动位置**表达的，
    /// 而滚动浏览并不会改 SelectedDate（它平时一直就等于今天）。
    /// 于是 SelectedDate 的 setter 里 SetProperty 判定"值没变"直接短路，
    /// 重建与滚动信号全被跳过 —— 表现就是点了「今天」什么都没发生。
    ///
    /// 所以这里强制走完三件事：锚点归位到今天所在月（今天已被时间轴裁掉时也能恢复）、
    /// 清空格子选中让右侧面板回到今日任务、**无条件**给视图一次滚回今天的信号。
    /// </summary>
    public void GoToday()
    {
        // 直接写字段：绕过 SetProperty 的"值未变就跳过"，日历重建在本方法里显式做。
        var dateChanged = _selectedDate != _today;
        _selectedDate = _today;
        if (dateChanged)
        {
            OnPropertyChanged(nameof(SelectedDate));
        }

        ClearCellSelection();

        // 锚点归位。注意 ExtendTimelineBack/Forward 只追加月份块、不动锚点，
        // 所以"滚远了"之后锚点还是旧值，必须在这里覆盖。
        _timelineAnchor = new DateOnly(_today.Year, _today.Month, 1);

        if (Settings.ViewMode == CalendarViewMode.Month)
        {
            // 今天可能早被 TimelineMaxMonths 裁到时间轴之外，必须重建结构。
            // BuildTimeline 内部会发 TimelineRebuilt。
            BuildTimeline();
            // 锚点此时已等于今天所在月，所以这里只会走增量刷新与派生属性通知。
            RebuildCalendar();
        }
        else
        {
            RebuildCalendar();
            // 周/年视图是照 SelectedDate 重建的，结构本身没问题，
            // 但滚动位置还要靠这一下信号才会拉回今天。
            TimelineRebuilt?.Invoke();
        }
    }

    public void SetHolidays(IEnumerable<ChinaHoliday> holidays)
    {
        _holidays = holidays.ToList();
        RebuildCalendar();
    }

    private double? _notifiedMonthCellHeight;
    private double? _notifiedWeekCellHeight;
    private double? _notifiedWeekPanelMaxHeight;
    private double? _notifiedYearCellHeight;
    private double? _notifiedYearMonthHeight;
    private double? _notifiedCurrentDayCellHeight;

    /// <summary>
    /// 只在高度真的变化时才发通知。
    /// 每次重建都无条件 OnPropertyChanged 会让所有日期格子重新设置 Height，
    /// 触发整轮重新布局；在虚拟化的月视图里这会让滚动位置漂移，
    /// 表现为"加个任务，左侧月份自己跳一下"。
    /// </summary>
    public void RefreshHeightProperties()
    {
        NotifyIfHeightChanged(nameof(MonthCellHeight), MonthCellHeight, ref _notifiedMonthCellHeight);
        NotifyIfHeightChanged(nameof(WeekCellHeight), WeekCellHeight, ref _notifiedWeekCellHeight);
        NotifyIfHeightChanged(nameof(WeekPanelMaxHeight), WeekPanelMaxHeight, ref _notifiedWeekPanelMaxHeight);
        NotifyIfHeightChanged(nameof(YearCellHeight), YearCellHeight, ref _notifiedYearCellHeight);
        NotifyIfHeightChanged(nameof(YearMonthHeight), YearMonthHeight, ref _notifiedYearMonthHeight);
        NotifyIfHeightChanged(nameof(CurrentDayCellHeight), CurrentDayCellHeight, ref _notifiedCurrentDayCellHeight);
    }

    private void NotifyIfHeightChanged(string propertyName, double value, ref double? cache)
    {
        if (cache.HasValue && Math.Abs(cache.Value - value) < 0.01)
        {
            return;
        }

        cache = value;
        OnPropertyChanged(propertyName);
    }

    public void RebuildCalendar()
    {
        if (Settings.ViewMode == CalendarViewMode.Month)
        {
            // 锚点月份变化才重建时间轴结构；否则仅增量刷新任务，避免滚动位置跳变。
            var anchorChanged = _timelineAnchor.Year != SelectedDate.Year
                                || _timelineAnchor.Month != SelectedDate.Month;
            if (anchorChanged)
            {
                _timelineAnchor = new DateOnly(SelectedDate.Year, SelectedDate.Month, 1);
                BuildTimeline();
            }
            else
            {
                RefreshTimelineTasks();
            }

            RefreshTodayTasks();
            RefreshWeekTasks();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(TodayDisplay));
            OnPropertyChanged(nameof(TodayTaskCount));
            OnPropertyChanged(nameof(ThisWeekTaskCount));
            RefreshHeightProperties();
            return;
        }

        // 重建前记录选中日期，重建后由 CreateDayCell 自动恢复高亮
        VisibleDays.Clear();
        YearMonths.Clear();

        if (Settings.ViewMode == CalendarViewMode.Year)
        {
            for (var month = 1; month <= 12; month++)
            {
                var days = CalendarService.BuildMonth(new DateOnly(SelectedDate.Year, month, 1), Today)
                    .Select(CreateDayCell)
                    .ToList();
                YearMonths.Add(new MonthSummaryViewModel(month, days));
            }
        }
        else
        {
            var days = Settings.ViewMode == CalendarViewMode.Week
                ? CalendarService.BuildWeek(SelectedDate, Today)
                : CalendarService.BuildMonth(SelectedDate, Today);

            foreach (var day in days.Select(CreateDayCell))
            {
                VisibleDays.Add(day);
            }
        }

        RefreshTodayTasks();
        RefreshWeekTasks();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TodayDisplay));
        OnPropertyChanged(nameof(TodayTaskCount));
        OnPropertyChanged(nameof(ThisWeekTaskCount));
        RefreshHeightProperties();
    }

    /// <summary>以锚点月份为中心，构建前后各 2 个月的时间轴（共 5 个月）。</summary>
    private void BuildTimeline()
    {
        var anchor = _timelineAnchor == default ? _today : _timelineAnchor;
        TimelineMonths.Clear();
        for (var offset = -TimelineInitialSpan; offset <= TimelineInitialSpan; offset++)
        {
            AddMonthBlock(anchor.AddMonths(offset));
        }

        TimelineRebuilt?.Invoke();
    }

    private void AddMonthBlock(DateOnly monthStart, bool atEnd = true)
    {
        var first = new DateOnly(monthStart.Year, monthStart.Month, 1);
        var cells = CalendarService.BuildMonth(first, _today)
            .Select(CreateDayCell)
            .ToList();
        var containsToday = cells.Any(cell => cell.IsToday);
        var block = new MonthBlockViewModel(first.Year, first.Month, cells, containsToday);
        if (atEnd)
        {
            TimelineMonths.Add(block);
        }
        else
        {
            TimelineMonths.Insert(0, block);
        }

        TrimTimeline(trimFromStart: atEnd);
    }

    /// <summary>
    /// 时间轴长度超过上限时，从与新增方向相反的一端裁掉多余月份块。
    /// 这是内存兜底：每个月份块都持有 42 个日期格子及其视觉元素，
    /// 一旦任何异常路径导致无限追加，内存会迅速涨到 GB 级并把界面拖死。
    /// </summary>
    private void TrimTimeline(bool trimFromStart)
    {
        while (TimelineMonths.Count > TimelineMaxMonths)
        {
            TimelineMonths.RemoveAt(trimFromStart ? 0 : TimelineMonths.Count - 1);
        }
    }

    /// <summary>在现有时间轴顶部追加更早的月份。</summary>
    public void ExtendTimelineBack()
    {
        if (TimelineMonths.Count == 0)
        {
            return;
        }

        var first = TimelineMonths[0];
        AddMonthBlock(new DateOnly(first.Year, first.Month, 1).AddMonths(-1), atEnd: false);
    }

    /// <summary>在现有时间轴底部追加更晚的月份。</summary>
    public void ExtendTimelineForward()
    {
        if (TimelineMonths.Count == 0)
        {
            return;
        }

        var last = TimelineMonths[^1];
        AddMonthBlock(new DateOnly(last.Year, last.Month, 1).AddMonths(1), atEnd: true);
    }

    /// <summary>
    /// 是否存在尚未提交的行内输入（日期格子里的新建输入框 / 任务重命名框）。
    /// 周期性刷新遇到它时应跳过，否则会重建 ViewModel 导致草稿丢失、输入框闪退。
    /// 今日任务面板使用独立的 ViewModel 实例并自带状态恢复，不计入此处。
    /// </summary>
    public bool HasPendingInlineInput =>
        VisibleDays.Any(HasPendingInput) ||
        YearMonths.SelectMany(month => month.Days).Any(HasPendingInput) ||
        TimelineMonths.SelectMany(block => block.Days).Any(HasPendingInput);

    private static bool HasPendingInput(DayCellViewModel cell)
    {
        return cell.IsAddingTask || cell.Tasks.Any(task => task.IsEditing);
    }

    /// <summary>仅刷新现有月份块内每个格子的任务/节假日，不重建结构（保留滚动位置）。</summary>
    /// <remarks>
    /// 用户主动操作（加/改/删任务）也会走这里，因此不能因为有草稿就跳过——
    /// 草稿与编辑态由 DayCellViewModel.Refresh 负责保留。
    /// 周期性的时钟刷新（RefreshClock）才会在有草稿时跳过。
    /// </remarks>
    public void RefreshTimelineTasks()
    {
        foreach (var block in TimelineMonths)
        {
            foreach (var cell in block.Days)
            {
                List<CalendarTask> tasks;
                lock (_syncRoot)
                {
                tasks = _data.Tasks
                    .Where(task => task.Date == cell.Date)
                    .OrderByDescending(task => task.IsImportant)
                    .ThenBy(task => task.CreatedAt)
                    .ToList();
                }

                var holidays = _holidays
                    .Where(holiday => holiday.Date == cell.Date)
                    .OrderBy(holiday => holiday.IsMakeupWorkday)
                    .ThenBy(holiday => holiday.Name)
                    .ToList();

                cell.Refresh(tasks, holidays);
            }
        }

        RefreshTodayTasks();
        RefreshWeekTasks();
        OnPropertyChanged(nameof(TodayTaskCount));
        OnPropertyChanged(nameof(ThisWeekTaskCount));
    }

    private DayCellViewModel CreateDayCell(CalendarDay day)
    {
        List<CalendarTask> tasks;
        lock (_syncRoot)
        {
            tasks = _data.Tasks
                .Where(task => task.Date == day.Date)
                .OrderByDescending(task => task.IsImportant)
                .ThenBy(task => task.CreatedAt)
                .ToList();
        }

        var taskViewModels = tasks.Select(task => new TaskItemViewModel(task, _nowProvider));
        var holidays = _holidays
            .Where(holiday => holiday.Date == day.Date)
            .OrderBy(holiday => holiday.IsMakeupWorkday)
            .ThenBy(holiday => holiday.Name);

        var cell = new DayCellViewModel(day.Date, day.IsInCurrentMonth, day.IsToday, taskViewModels, holidays);
        cell.IsSelected = _selectedCellDate == day.Date;
        return cell;
    }

    private CalendarTask? FindTask(Guid taskId)
    {
        return _data.Tasks.FirstOrDefault(task => task.Id == taskId);
    }

    private static DateOnly GetWeekStart(DateOnly date)
    {
        return date.AddDays(-(int)date.DayOfWeek);
    }
}
