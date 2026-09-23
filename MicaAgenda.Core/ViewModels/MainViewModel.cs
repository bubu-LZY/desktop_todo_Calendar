using System.Collections.ObjectModel;
using System.Globalization;
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

    /// <summary>
    /// 周视图首屏一次性铺多少周的日期格子。
    ///
    /// 周视图的左栏是可上下滚动的长列表：默认停在"最近 7 天"开头，往下滚就能看到后面的日期。
    /// 一次铺 8 周（56 天）足够用户滚好一会儿而不用等补建；滚到接近底部时由
    /// <see cref="ExtendWeekScroll"/> 再追加，所以这里给的是"起步量"而不是上限。
    /// </summary>
    public const int WeekScrollInitialWeeks = 8;

    /// <summary>周视图每次向后续追加的周数（滚到接近底部时触发）。</summary>
    public const int WeekScrollAppendWeeks = 4;

    /// <summary>
    /// 周视图左栏滚动列表的天数上限。向下滚动会不断向后追加日期，
    /// 每个格子都带着 3 个 ObservableCollection 与事件订阅 —— 不设上限内存会单调增长，
    /// 挂机越久越卡。超出后从头部裁剪（头部的日期都是用户已经滚过去的，安全丢弃）。
    /// 裁剪后宿主需要按去掉的行数补偿滚动偏移，见 <see cref="WeekScrollHeadTrimmed"/>。
    /// </summary>
    public const int WeekScrollMaxDays = 112;

    /// <summary>
    /// 周视图左栏从头部裁剪掉若干天后触发（参数为裁剪的天数）。
    /// 宿主据此把滚动偏移下调 <c>裁剪天数 × 单行高度</c>，避免视口内容跳变。
    /// </summary>
    public event Action<int>? WeekScrollHeadTrimmed;

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
    private TimeSpan? _todayTaskTime = CalendarTask.DefaultTime.ToTimeSpan();
    private bool _todayTaskTimePopupOpen;

    /// <summary>本周三个分组共用的 VM 实例池（按任务 Id），保证跨组移动时实例不销毁。</summary>
    private readonly Dictionary<Guid, TaskItemViewModel> _weekVmPool = new();

    /// <summary>上一次全量重推派生文本的时刻，用于节流。</summary>
    private DateTimeOffset _lastDerivedTextRefresh = DateTimeOffset.MinValue;

    /// <summary>上一次时钟 tick 时的「未完成且已逾期」任务 Id 集合，用于检测日内跨时刻变化。</summary>
    private HashSet<Guid>? _lastOverdueTaskIds;

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
        TodayTaskLeadLabels = [];
        TodayTaskLeadLabels.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(TodayTaskLeadSummary));
        SetYearCommand = new RelayCommand(_ => SetViewMode(CalendarViewMode.Year));
        SetMonthCommand = new RelayCommand(_ => SetViewMode(CalendarViewMode.Month));
        SetWeekCommand = new RelayCommand(_ => SetViewMode(CalendarViewMode.Week));
        SetTaskCommand = new RelayCommand(_ => SetViewMode(CalendarViewMode.Tasks));
        PreviousCommand = new RelayCommand(_ => MovePrevious());
        NextCommand = new RelayCommand(_ => MoveNext());
        TodayCommand = new RelayCommand(_ => GoToday());

        // 先把周期地平线推到今天，再建日历。
        // 顺序反了的话，这次补出来的实例要等到下一次重建才看得见（用户会以为"没生效"）。
        EnsureRecurrenceHorizon();

        BuildTimeline();
        RebuildCalendar();
    }

    /// <summary>
    /// 本次构造 / 跨天时，是否真的补出过周期实例。
    /// 宿主用它决定"要不要立刻落盘"，单测用它断言幂等（第二次调用必须为 false）。
    /// </summary>
    public bool RecurrenceHorizonExtended { get; private set; }

    /// <summary>
    /// 把周期系列的物化地平线推到今天之后（见 <see cref="Services.RecurrenceService.TopUp"/>）。
    ///
    /// <para>不这么做的话，封顶挂在模板日期上，系列铺满 2 年就永久用完 ——
    /// 用户看到的就是"周期任务最多只能加 731 个"。放在跨天路径上，系列会随日子自动续期。</para>
    /// </summary>
    private bool EnsureRecurrenceHorizon()
    {
        List<CalendarTask> added;
        lock (_syncRoot)
        {
            added = RecurrenceService.TopUp(_data.Tasks, _today);
            if (added.Count == 0)
            {
                return false;
            }

            _data.Tasks.AddRange(added);
        }

        RecurrenceHorizonExtended = true;
        MarkDirty();
        return true;
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

    /// <summary>纯任务视图：主区不显示日历格子，只留「今日 + 本周」任务面板。</summary>
    public bool IsTaskView => Settings.ViewMode == CalendarViewMode.Tasks;

    /// <summary>
    /// 月 / 年视图共用的那一套「左侧日历 + 右侧任务面板」布局是否在显示。
    /// 周视图的任务面板在日期格子**下面**、占满剩余高度；任务视图则完全没有日历 ——
    /// 后两者都要收起这套布局，免得面板从底下透出来。
    /// </summary>
    public bool IsMonthOrYearView =>
        Settings.ViewMode is CalendarViewMode.Month or CalendarViewMode.Year;

    /// <summary>
    /// 窗口是否已窄到只显示「今日任务」这一块（宿主在 <c>UpdateResponsiveLayout</c> 里回填）。
    ///
    /// 窄屏下主体被强制换成任务面板，跟 <see cref="Settings"/>.ViewMode 已经没关系了，
    /// 但下拉按钮上的文字如果还照着 ViewMode 显示，就会出现「画面是任务列表、按钮却写着周视图」
    /// 这种自相矛盾的状态。所以宿主缩放时要把这件事同步进来，让 <see cref="CurrentViewLabel"/>
    /// 与<b>实际画出来的东西</b>一致。
    /// </summary>
    public bool IsNarrowTaskOnly
    {
        get => _isNarrowTaskOnly;
        set
        {
            if (_isNarrowTaskOnly == value)
            {
                return;
            }

            _isNarrowTaskOnly = value;
            OnPropertyChanged();
            // 标签是窄屏状态的派生值，必须跟着一起通知。
            OnPropertyChanged(nameof(CurrentViewLabel));
        }
    }

    private bool _isNarrowTaskOnly;

    /// <summary>
    /// 顶栏视图下拉按钮上显示的当前视图名（与下拉项文本保持一致）。
    ///
    /// 窄屏下主体只剩任务面板，无论之前选的是月/周/年，用户看到的都是任务列表 ——
    /// 这时标签一律报「任务视图」，否则按钮文字和画面会对不上。
    /// </summary>
    public string CurrentViewLabel
    {
        get
        {
            if (IsNarrowTaskOnly)
            {
                return "任务视图";
            }

            return Settings.ViewMode switch
            {
                CalendarViewMode.Month => "月视图",
                CalendarViewMode.Week => "周视图",
                CalendarViewMode.Year => "年视图",
                CalendarViewMode.Tasks => "任务视图",
                _ => "月视图"
            };
        }
    }

    /// <summary>今日任务面板数据源（独立于日历格子中的 ViewModel 实例，互不干扰）。</summary>
    public ObservableCollection<TaskItemViewModel> TodayTasks { get; }

    /// <summary>
    /// 合并后的「未完成」组：所有已逾期的未完成任务（含历史欠账）排在前面，
    /// 后面接本周（今天所在的那一周）还没到任务时刻的任务。
    /// 逾期任务在行内挂红色【已逾期】前缀（见 <see cref="TaskItemViewModel.IsOverdueNow"/>），
    /// 不再单列分组。始终基于"今天"所在的周，不随选中的日期变化。
    /// </summary>
    public ObservableCollection<TaskItemViewModel> WeekOpenTasks { get; } = new();

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
    private bool _isDirty;

    /// <summary>
    /// 数据变更标记。setter 会触发 PropertyChanged —— 宿主订阅它来启动「脏了才落盘」的防抖。
    /// 这里做成"赋值即通知"，而不是只靠 <see cref="MarkDirty"/>：
    /// VM 内部有大量直接 <c>IsDirty = true</c> 的路径（加/改/删任务、勾选完成等），
    /// 若只让 MarkDirty 通知，这些路径改完数据后防抖根本不会启动、自动保存就断了。
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value)
            {
                return;
            }

            _isDirty = value;
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    /// <summary>标记数据已变（对外入口，供宿主在无法经过 VM 内部赋值路径时调用）。</summary>
    public void MarkDirty() => IsDirty = true;

    public void MarkSaved() => IsDirty = false;
    public RelayCommand SetYearCommand { get; }
    public RelayCommand SetMonthCommand { get; }
    public RelayCommand SetWeekCommand { get; }
    public RelayCommand SetTaskCommand { get; }
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
        CalendarViewMode.Tasks => "任务视图",
        _ => $"{SelectedDate:yyyy年M月}"
    };

    public string ClockText => _nowProvider().LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>今日日期星期显示（如："8月25日 周二"）</summary>
    public string TodayDisplay
    {
        get
        {
            var now = _nowProvider().LocalDateTime;
            return $"{now:M月d日} {Helpers.WeekdayText.Of(now.DayOfWeek)}";
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

    // ===== 本周任务完成情况（始终基于今天所在的那一周；未分组合并逾期 + 未到期） =====

    /// <summary>「未完成」组总数（逾期欠账 + 本周待办）。</summary>
    public int WeekOpenCount => WeekOpenTasks.Count;

    /// <summary>「未完成」组里此刻已经逾期的数量（标题旁的红色小计数用）。</summary>
    public int WeekOverdueCount { get; private set; }

    /// <summary>本周已完成任务数。</summary>
    public int WeekCompletedCount => WeekCompletedTasks.Count;

    /// <summary>本周总任务数（未完成 + 已完成两组合计）。</summary>
    public int WeekTotalCount => WeekOpenCount + WeekCompletedCount;

    public bool HasWeekOpen => WeekOpenCount > 0;
    public bool HasWeekCompleted => WeekCompletedCount > 0;

    /// <summary>合并后的「未完成」组里是否含逾期欠账（组标题旁的红色「N 项已逾期」用）。</summary>
    public bool HasWeekOverdue => WeekOverdueCount > 0;

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

    /// <summary>多选下拉里可勾选的提醒档位（不含「不提醒」；与日期格子内快速添加共用同一份）。</summary>
    public IReadOnlyList<string> ReminderLeadOptions { get; } = Helpers.ReminderLeadCatalog.SelectableLabels;

    /// <summary>
    /// 月视图表头的星期单字（日/一/二 … 六），顺序为「周日 → 周六」，与日期网格的铺法一致。
    /// 走 <see cref="Helpers.WeekdayText.HeaderChars"/> 的唯一一份：
    /// 原先 XAML 里手写了这七个字符，一旦网格起点改成周一就会静默错位（表头与格子差一列）。
    /// </summary>
    public IReadOnlyList<string> WeekHeaderChars { get; } = Helpers.WeekdayText.HeaderChars;

    /// <summary>今日面板快速添加时勾选的提醒档位标签集合（多选；空集合 = 不提醒）。</summary>
    public ObservableCollection<string> TodayTaskLeadLabels { get; }

    /// <summary>下拉按钮上的摘要：空 = 「不提醒」，否则把勾选项顿号连起来。</summary>
    public string TodayTaskLeadSummary
        => Helpers.ReminderLeadCatalog.Summarize(TodayTaskLeadLabels);

    /// <summary>
    /// 草稿勾选的全部提前提醒量（分钟）：空集合 = 这条任务不推；0 = 「到时提醒」。
    /// </summary>
    public IReadOnlyList<int> TodayTaskReminderLeads
        => Helpers.ReminderLeadCatalog.ToMinutesList(TodayTaskLeadLabels);

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
                OnPropertyChanged(nameof(TodayTaskTimeText));
            }
        }
    }

    /// <summary>草稿任务时刻对应的 <see cref="TimeOnly"/>（落盘时用它）。</summary>
    public TimeOnly TodayTaskTimeOnly => TimeOnly.FromTimeSpan(_todayTaskTime ?? CalendarTask.DefaultTime.ToTimeSpan());

    /// <summary>
    /// 草稿任务时刻的展示文本（HH:mm）。紧凑添加表单的时间小按钮直接拿它当标签，
    /// 一眼能看到当前选的是几点，不用点开才知道。
    /// </summary>
    public string TodayTaskTimeText => TodayTaskTimeOnly.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 右侧快速添加表单里「时间」小按钮的弹层是否打开。
    ///
    /// <para>放在 VM 而不是用 XAML 的 <c>#TimeToggle</c> 名字引用：这个模板在宿主里被实例化了
    /// 多份（右侧面板 + 窄窗视图 + 各视图布局），<c>x:Name</c> 在 DataTemplate 里跨实例解析不可靠，
    /// 弹层会打不开（用户反馈"点时间变成蓝色、但没法设置具体时间"）。
    /// 用 VM 属性让 ToggleButton 的 IsChecked 与 Popup 的 IsOpen 都绑它，天然同步、与实例数无关。</para>
    /// </summary>
    public bool TodayTaskTimePopupOpen
    {
        get => _todayTaskTimePopupOpen;
        set => SetProperty(ref _todayTaskTimePopupOpen, value);
    }

    /// <summary>展开今日任务的快速输入框。</summary>
    public void BeginAddTodayTask()
    {
        TodayTaskDraft = string.Empty;
        ResetLeadLabel();
        TodayTaskTimePopupOpen = false;
        IsAddingTodayTask = true;
    }

    /// <summary>收起今日任务的快速输入框。</summary>
    public void CancelTodayTask()
    {
        TodayTaskDraft = string.Empty;
        ResetLeadLabel();
        TodayTaskTimePopupOpen = false;
        IsAddingTodayTask = false;
    }

    /// <summary>
    /// 提交今日任务的快速输入，返回新建的任务（草稿为空则返回 null）。
    /// 任务的时刻由表单里选（默认当天 9:00），提前提醒量相对它往前推。
    ///
    /// 提交后<b>整套草稿状态都要复位</b>：标题清空、提醒不勾、时刻回 9:00，
    /// 并且<b>面板日期回到今天</b>——否则在某个日期格子里加完一条，下一次快速添加
    /// 还会静默落到同一个日期，用户以为加到了今天。
    /// （历史现象：输入框空了，但日期/时刻还留着上一次的值。）
    /// </summary>
    public CalendarTask? CommitTodayTask()
    {
        var title = TodayTaskDraft.Trim();
        // 含「不提醒」→ 空列表；空（未指定）→ null（默认提前15分钟）；否则档位列表。
        var leads = Helpers.ReminderLeadCatalog.ToCommitLeads(TodayTaskLeadLabels);
        var time = TodayTaskTimeOnly;
        // 面板日期要在复位之前取：它是本条任务实际归属的日期。
        var date = _panelDate;

        ResetQuickAddDraft();
        IsAddingTodayTask = false;

        return string.IsNullOrWhiteSpace(title)
            ? null
            : AddTask(date, title, leads, time);
    }

    /// <summary>
    /// 把快速添加的整套草稿状态复位成"刚展开"的样子：
    /// 标题清空、提醒一个都不勾、任务时刻回到当天 9:00。
    ///
    /// 单列一个方法是为了让「展开时」与「提交后」共用同一份复位逻辑，
    /// 避免两处各写一半、日后再加字段时漏掉其中一边。
    /// </summary>
    private void ResetQuickAddDraft()
    {
        TodayTaskDraft = string.Empty;
        ResetLeadLabel();
        TodayTaskTimePopupOpen = false;

        // 面板日期也要复位 —— 否则在某个日期格子里加完一条后，下一次快速添加会静默落到
        // 同一个旧日期，用户以为加到了今天。
        //
        // 但「用户明确选中了某个日期格子」是刻意动作，不能夺走：那种情况下任务本就该记到
        // 那一天，面板也应继续停在那一天，方便连着加好几条。所以只有没选中格子时才回今天。
        if (_selectedCellDate is null)
        {
            SetPanelDate(_today);
        }
    }

    /// <summary>提示档位与时刻复位：提醒一个都不勾、任务时刻回到当天 9:00。</summary>
    private void ResetLeadLabel()
    {
        TodayTaskLeadLabels.Clear();
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
            tasks = OrderForDay(_data.Tasks.Where(task => task.Date == _panelDate)).ToList();
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
    /// 清空合并后的"未完成"组：逾期欠账 + 本周内还没到点的待办，一起删。返回删除数。
    /// 与 <see cref="RefreshWeekTasks"/> 显示的那一组同一个口径，
    /// 否则按钮删掉的集合会跟界面上显示的对不上。
    /// </summary>
    public int ClearOpenTasks()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);
        var nowLocal = _nowProvider().LocalDateTime;
        return BulkRemove(t => IsOverdueTask(t, nowLocal) || IsOpenTask(t, nowLocal, weekStart, weekEnd));
    }

    /// <summary>清空"已完成"组的全部任务。返回删除数。
    /// 谓词与 <see cref="RefreshWeekTasks"/> 的显示口径一致（计划日期在本周或完成于本周），
    /// 否则一条历史日期、本周刚勾完的任务在组里可见却删不掉。</summary>
    public int ClearCompletedTasks()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);
        return BulkRemove(t => t.IsCompleted && IsInWeek(t, weekStart, weekEnd));
    }

    /// <summary>把合并后的"未完成"组（逾期欠账 + 本周待办）全部标记为已完成。返回处理数。</summary>
    public int MarkOpenCompleted()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);
        var now = _nowProvider();
        return BulkUpdate(
            t => IsOverdueTask(t, now.LocalDateTime) || IsOpenTask(t, now.LocalDateTime, weekStart, weekEnd),
            t => t.MarkCompleted(now));
    }

    /// <summary>把"已完成"组的全部任务标记为未完成。返回处理数。
    /// 口径同 <see cref="ClearCompletedTasks"/>：计划日期在本周或完成于本周。</summary>
    public int MarkCompletedIncomplete()
    {
        var weekStart = GetWeekStart(Today);
        var weekEnd = weekStart.AddDays(6);
        return BulkUpdate(
            t => t.IsCompleted && IsInWeek(t, weekStart, weekEnd),
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

        List<CalendarTask> mergedOpen;
        List<CalendarTask> completed;
        lock (_syncRoot)
        {
            // 合并后的「未完成」：逾期欠账（日期最早的排最前）在前，本周待办按日期接在后面。
            //
            // 周期任务在这一组里**只压到"同一天内的最后"**，不做全局垫底：
            // 这一组跨多天，而"逾期欠账排最前"是既有明确规则（见上面那行注释）。
            // 若让周期任务整段垫底，一条上周的逾期周期任务会被排到本周普通任务之后，
            // 等于把最该催的欠账藏起来 —— 那不是用户想要的效果。
            var overdue = _data.Tasks
                .Where(t => IsOverdueTask(t, nowLocal))
                .OrderBy(t => t.Date)
                .ThenBy(t => t.Time ?? CalendarTask.DefaultTime)
                .ThenBy(t => t.IsRecurring)
                .ThenBy(t => t.CreatedAt)
                .ToList();
            var open = _data.Tasks
                .Where(t => IsOpenTask(t, nowLocal, weekStart, weekEnd))
                .OrderBy(t => t.Date)
                .ThenBy(t => t.Time ?? CalendarTask.DefaultTime)
                .ThenBy(t => t.IsRecurring)
                .ThenBy(t => t.CreatedAt)
                .ToList();
            mergedOpen = [..overdue, ..open];

            // 判定用「计划日期在本周」或「实际完成于本周」的并集：
            // 只用 Date 判定的话，一条 8 月的逾期任务在今天勾完就会从组里凭空消失
            // （既不再是"逾期未完成"，也不算"本周完成"），看起来像任务丢了。
            // 「已完成」这组允许周期任务整段垫底：它本来就是"看战果"的列表，顺序无关紧要。
            completed = _data.Tasks
                .Where(t => t.IsCompleted && IsInWeek(t, weekStart, weekEnd))
                .OrderBy(t => t.IsRecurring)
                .ThenByDescending(t => t.CompletedAt ?? t.CreatedAt)
                .ThenBy(t => t.CreatedAt)
                .ToList();

            WeekOverdueCount = overdue.Count;
        }

        var openVms = mergedOpen.Select(GetWeekVm).ToList();
        var completedVms = completed.Select(GetWeekVm).ToList();

        Reconcile(WeekOpenTasks, openVms);
        Reconcile(WeekCompletedTasks, completedVms);

        PruneWeekVmPool(openVms, completedVms);

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
    /// 清空所有「已逾期未完成」任务（只动逾期欠账，不含本周待办）。返回删除数。
    /// Avalonia 宿主的分组合并后入口统一走 <see cref="ClearOpenTasks"/>；
    /// WPF 宿主仍保留独立的「逾期未完成」组，继续用这个方法。
    /// </summary>
    public int ClearOverdueTasks() => BulkRemove(t => IsOverdueTask(t, _nowProvider().LocalDateTime));

    /// <summary>把所有「已逾期未完成」任务标记为已完成（只动逾期欠账）。返回处理数。WPF 宿主仍在用。</summary>
    public int MarkOverdueCompleted()
    {
        var now = _nowProvider();
        return BulkUpdate(t => IsOverdueTask(t, now.LocalDateTime), t => t.MarkCompleted(now));
    }

    /// <summary>
    /// 「逾期未完成」：未完成，且已经过了任务时刻（日期 + 时间，没设时间按当天 9:00）。
    /// 右侧分组合并后，清空/批量完成按钮与 <see cref="RefreshWeekTasks"/> 都走这一个口径。
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

    /// <summary>年视图日期数字字号（默认 11，宿主按月卡实际列宽回填）。</summary>
    public double YearDayFontSize { get; private set; } = 11;

    /// <summary>年视图节日徽标字号，随日期字号一起缩（默认 9）。</summary>
    public double YearBadgeFontSize { get; private set; } = 9;

    private double _yearFontScale = 1.0;

    /// <summary>
    /// 按缩放系数同步回填年视图的两组字号。
    ///
    /// 窗口缩小时年视图月卡的列宽跟着变小 —— 字号不同步缩小的话，两位日期数字会被右侧的
    /// 节日徽标盖住或直接裁掉（用户实测"缩小之后每天的数字都看不清楚了"）。
    /// 缩放系数由宿主按「列宽 ÷ 基准列宽 40px」算出，这里只负责夹取与量化：
    /// 量化到 0.02 步进，拖窗口时不会产生连续的属性通知与无谓重排；上限 1.0 保持
    /// 原版观感（不在大窗口下放大），下限 0.62（数字 ≈7px，再小就真的不可辨了）。
    /// </summary>
    public void SetYearFontScale(double scale)
    {
        // 防御 NaN / Infinity：这个系数由宿主按「列宽 ÷ 基准列宽」算出，
        // 而列宽在首帧、视图刚切换、或极端布局下可能是 0 —— 除出来就是 NaN。
        // NaN 会一路穿过 Clamp/Round 变成**字号的 NaN**，而字号为 NaN 时
        // 整片日期数字会直接不渲染（用户实测："年视图里日期数字全不见了，只剩节日徽标"）。
        if (double.IsNaN(scale) || double.IsInfinity(scale))
        {
            scale = 1.0;
        }

        var quantized = Math.Round(Math.Clamp(scale, 0.62, 1.0) / 0.02) * 0.02;
        if (Math.Abs(quantized - _yearFontScale) < 0.001)
        {
            return;
        }

        _yearFontScale = quantized;
        YearDayFontSize = Math.Round(11 * quantized, 1);
        YearBadgeFontSize = Math.Round(9 * quantized, 1);
        OnPropertyChanged(nameof(YearDayFontSize));
        OnPropertyChanged(nameof(YearBadgeFontSize));
    }

    /// <summary>
    /// 周视图左栏的固定宽度。
    ///
    /// 左栏日期格子<b>只吃这个宽度，不随窗口缩放变化</b> —— 用户的要求是"调窗口大小时调的是
    /// 右侧今日任务面板的宽度，左侧日期格子宽度保持默认、不跟着变"。
    /// 所以这是个常量，不再由宿主按高度回填（早期版本让它跟高度联动做成正方形，
    /// 结果格子内部留出大片空白、右面板反而被挤窄，已废弃）。
    ///
    /// 132 是用户实测"152 太宽"后收窄的值。注意 XAML 里还有同值的硬编码
    ///（周视图左列的 Border 的 Width），改这里时要一并改掉。
    ///
    /// <para>周视图左列（ColumnDefinition）比它**宽 <see cref="WeekScrollBarGutter"/>**，
    /// 多出来的那一条是滚动条沟槽 —— 见下面常量的说明。</para>
    /// </summary>
    public const double WeekScrollColumnWidth = 132;

    /// <summary>
    /// 周视图左列留给纵向滚动条的沟槽宽度（列宽 = <see cref="WeekScrollColumnWidth"/> + 它）。
    ///
    /// <para>滚动条是 <c>ScrollViewer</c> 模板的一部分、画在自己的右边缘：左列刚好等于格子宽度时，
    /// 那条滚动条就压在最右侧的日期格子上（用户反馈"压住我的每一个日期格子，感觉很丑"）。
    /// 把列宽多留一条沟槽、格子本身固定 132 且左对齐，滚动条就落进缝隙里了。</para>
    /// </summary>
    public const double WeekScrollBarGutter = 10;

    /// <summary>
    /// 周视图左栏日期格子的高度，由宿主的 <c>UpdateResponsiveLayout</c> 写入。
    ///
    /// 高度是唯一跟随窗口变化的量：宿主按「左栏可用高度 ÷ 7」算出，让 7 个格子刚好铺满
    /// 当前界面高度（纵向不浪费）。宽度固定为 <see cref="WeekScrollColumnWidth"/>，
    /// 所以格子是"宽固定、高自适应"的矩形，不再强行正方形。
    /// 宿主回填 0 时 XAML 里的兜底值生效。
    /// </summary>
    public double WeekScrollCellHeight
    {
        get => _weekScrollCellHeight;
        set
        {
            if (Math.Abs(_weekScrollCellHeight - value) < 0.01)
            {
                return;
            }

            _weekScrollCellHeight = value;
            OnPropertyChanged();
        }
    }

    private double _weekScrollCellHeight;

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
            // 跨天的第一件事：把周期地平线往前推（可能补出今天之后 2 年内的实例），
            // 再重建日历，新补出来的实例才会一起出现在格子里。
            EnsureRecurrenceHorizon();

            if (Settings.ViewMode == CalendarViewMode.Month)
            {
                RefreshTimelineTasks();
            }
            else
            {
                RebuildCalendar();
            }

            // 重建已经按最新时刻算过分组，顺手把基线换掉，免得下一分钟误判成"集合变化"
            OverdueSetChanged();
        }
        else if (OverdueSetChanged())
        {
            // 日内跨过某条未完成任务的时刻：它会从「未完成」语义变成「逾期」。
            // 行内红色【已逾期】前缀由各行的时钟通知自行刷新，但本周分组标题上的
            // 「N 项已逾期」计数 / 红点只在 RefreshWeekTasks 里重算，这里补一次。
            // （分组合并后组成员不变、排序也天然稳定，只需重算计数，成本很低。）
            RefreshWeekTasks();
        }

        // 让"未完成 N 天 / 逾期 N 天"这类与时间相关的文本跟着走。
        // 悬浮提示本身是在弹出时实时计算的，这里主要服务于行内徽标。
        RefreshDerivedText(force: dayChanged);
    }

    /// <summary>
    /// 重新计算「未完成且已逾期」任务的 Id 集合并与上一次基线比较；
    /// 无论是否变化都会把基线更新为当前集合。首次调用（基线为 null）返回 true。
    /// </summary>
    private bool OverdueSetChanged()
    {
        HashSet<Guid> current;
        var nowLocal = _nowProvider().LocalDateTime;
        lock (_syncRoot)
        {
            current = _data.Tasks
                .Where(t => !t.IsCompleted && t.IsOverdueAt(nowLocal))
                .Select(t => t.Id)
                .ToHashSet();
        }

        var changed = _lastOverdueTaskIds is null
                      || !_lastOverdueTaskIds.SetEquals(current);
        _lastOverdueTaskIds = current;
        return changed;
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
        => AddTask(date, title, reminderLeadMinutes is null ? null : [reminderLeadMinutes.Value], time);

    /// <summary>
    /// 多选提醒版添加任务：<paramref name="reminderLeads"/> 为勾选的全部提前量（分钟）。
    /// null = 未指定，默认带「提前 15 分钟」；空集合 = 明确「不提醒」；0 = 「到时提醒」。
    /// 第一个成为主提醒，其余进额外档位。
    /// </summary>
    public CalendarTask AddTask(DateOnly date, string title, IReadOnlyList<int>? reminderLeads, TimeOnly? time)
    {
        CalendarTask task;
        lock (_syncRoot)
        {
            task = new CalendarTask
            {
                Date = date,
                Title = title.Trim(),
                CreatedAt = _nowProvider(),
                Time = NormalizeTaskTime(time)
            };
            task.SetReminderLeads(reminderLeads ?? [Helpers.ReminderLeadCatalog.DefaultLeadMinutes]);
            // 新建时触发时刻已经过去（且超出该档位补发宽限）的档位直接记为已推：
            // 例如给今天的任务勾「提前一天」，保存瞬间不该蹦出一条陈旧提醒。
            task.SuppressMissedLeadReminders(_nowProvider());
            _data.Tasks.Add(task);
            IsDirty = true;
        }

        // RebuildCalendar 内含 UI 集合增删与属性通知，放到锁外执行，
        // 避免后台轮询 / API 线程在整轮界面重建期间被堵住。
        RebuildCalendar();
        return task;
    }

    /// <summary>
    /// 添加一条周期任务：创建源任务（第一次发生）+ 物化后续重复实例。
    /// 返回源任务；后续实例由 <see cref="RecurrenceService.Expand"/> 生成，SeriesId 指向源任务。
    /// </summary>
    public CalendarTask AddRecurringTask(
        DateOnly start,
        string title,
        RecurrenceFrequency frequency,
        int interval,
        DateOnly? end,
        TimeOnly? time,
        IReadOnlyList<int>? reminderLeads)
    {
        if (frequency == RecurrenceFrequency.None)
        {
            // 频率无效时退化为普通一次性任务。
            return AddTask(start, title, reminderLeads, time);
        }

        CalendarTask master;
        lock (_syncRoot)
        {
            master = new CalendarTask
            {
                Date = start,
                Title = title.Trim(),
                CreatedAt = _nowProvider(),
                Time = NormalizeTaskTime(time),
                Recurrence = frequency,
                RecurrenceInterval = Math.Max(1, interval),
                RecurrenceEnd = end,
            };
            master.SetReminderLeads(reminderLeads ?? [Helpers.ReminderLeadCatalog.DefaultLeadMinutes]);
            master.SuppressMissedLeadReminders(_nowProvider());
            _data.Tasks.Add(master);

            foreach (var instance in RecurrenceService.Expand(master))
            {
                // 过去日期的实例（用户给过去的起始日期时）同样压掉已错过的陈旧提醒。
                instance.SuppressMissedLeadReminders(_nowProvider());
                _data.Tasks.Add(instance);
            }

            IsDirty = true;
        }

        RebuildCalendar();
        return master;
    }

    /// <summary>删除整个周期任务系列（源任务 + 全部物化实例）。返回删除条数（0 = 系列不存在）。</summary>
    public int DeleteRecurringSeries(Guid seriesId)
    {
        int removed;
        lock (_syncRoot)
        {
            removed = _data.Tasks.RemoveAll(task => task.Id == seriesId || task.SeriesId == seriesId);
            if (removed > 0)
            {
                IsDirty = true;
            }
        }

        if (removed > 0)
        {
            RebuildCalendar();
        }

        return removed;
    }

    /// <summary>返回指定系列（含源任务与其全部实例）的任务列表；系列不存在返回空列表。</summary>
    public List<CalendarTask> GetRecurringSeries(Guid seriesId)
    {
        lock (_syncRoot)
        {
            return _data.Tasks
                .Where(task => task.Id == seriesId || task.SeriesId == seriesId)
                .ToList();
        }
    }

    /// <summary>返回所有周期任务系列的源任务（Recurrence != None），供管理面板列出。</summary>
    public List<CalendarTask> GetAllRecurringMasters()
    {
        lock (_syncRoot)
        {
            return _data.Tasks
                .Where(task => task.Recurrence != RecurrenceFrequency.None)
                .OrderBy(task => task.Date)
                .ThenBy(task => task.Title, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>删除所有周期任务系列（源任务 + 全部物化实例）。返回删除的系列数。</summary>
    public int DeleteAllRecurringSeries()
    {
        int seriesCount;
        lock (_syncRoot)
        {
            var masterIds = _data.Tasks
                .Where(task => task.Recurrence != RecurrenceFrequency.None)
                .Select(task => task.Id)
                .ToHashSet();
            seriesCount = masterIds.Count;

            _data.Tasks.RemoveAll(task =>
                task.Recurrence != RecurrenceFrequency.None
                || (task.SeriesId is { } sid && masterIds.Contains(sid)));

            if (seriesCount > 0)
            {
                IsDirty = true;
            }
        }

        if (seriesCount > 0)
        {
            RebuildCalendar();
        }

        return seriesCount;
    }

    /// <summary>
    /// 落盘前的任务时刻归一化：没选、或正好是当天默认时刻（9:00）都记 null。
    /// 免得同一条任务因为"显式选了 9:00"和"没选"而存成两种形态，
    /// 也让与 my-mindmap agent 同步来的复习任务（那边没有时间）保持同一种写法。
    /// </summary>
    private static TimeOnly? NormalizeTaskTime(TimeOnly? time)
        => time is null || time == CalendarTask.DefaultTime ? null : time;

    public void RenameTask(Guid taskId, string title)
    {
        bool changed;
        lock (_syncRoot)
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return;
            }

            task.Title = title.Trim();
            IsDirty = true;
            changed = true;
        }

        if (changed)
        {
            RebuildCalendar();
        }
    }

    /// <summary>
    /// 拖拽改期：把任务挪到另一个日期格子。改期后作废旧的「已推」提醒标记（新日期按新时刻重新提醒）。
    /// 返回是否真的改动了（任务不存在或日期未变返回 false）。
    /// </summary>
    public bool ChangeTaskDate(Guid taskId, DateOnly newDate)
    {
        bool changed;
        lock (_syncRoot)
        {
            var task = FindTask(taskId);
            if (task is null || task.Date == newDate)
            {
                return false;
            }

            task.Date = newDate;
            task.ResetReminder();
            IsDirty = true;
            changed = true;
        }

        if (changed)
        {
            RebuildCalendar();
        }

        return changed;
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
    /// <returns>任务存在并已写回返回 true；找不到（刚被删掉）返回 false。</returns>
    public bool UpdateTask(Guid taskId, string title, TimeOnly? time, int? reminderLeadMinutes)
        => UpdateTask(taskId, title, time,
            reminderLeadMinutes is null ? null : [reminderLeadMinutes.Value]);

    /// <summary>
    /// 多选提醒版编辑任务：<paramref name="reminderLeads"/> 为勾选的全部提前量（分钟），
    /// 空集合 / null = 不提醒。时刻或档位真的变了就作废全部档位的「已推」标记。
    /// </summary>
    public bool UpdateTask(Guid taskId, string title, TimeOnly? time, IReadOnlyList<int>? reminderLeads)
    {
        bool scheduleChanged;
        lock (_syncRoot)
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return false;
            }

            var newTitle = title.Trim();
            var newTime = NormalizeTaskTime(time);
            var newLeads = (reminderLeads ?? [])
                .Select(lead => Math.Max(0, lead))
                .Distinct()
                .ToList();
            var oldLeads = task.AllReminderLeads;

            if (newTitle.Length > 0)
            {
                task.Title = newTitle;
            }

            scheduleChanged = task.Time != newTime || !oldLeads.SequenceEqual(newLeads);
            task.Time = newTime;
            task.SetReminderLeads(newLeads);
            if (scheduleChanged)
            {
                task.ResetReminder();
                // 改期/改时刻后，新计划里已经过了补发窗口的档位直接视为已推，
                // 否则把任务改到今天并勾「提前一天」，保存瞬间就会收到一条陈旧提醒。
                task.SuppressMissedLeadReminders(_nowProvider());
            }

            IsDirty = true;
        }

        // UI 重建放锁外（见 AddTask 同样的理由）
        RebuildCalendar();
        return true;
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

            if (task.IsCompleted)
            {
                task.MarkIncomplete();
            }
            else
            {
                task.MarkCompleted(_nowProvider());
            }

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

        // 视图容器的可见性绑的是下面这几个单层布尔值（见属性注释），
        // 换视图时必须一起通知，否则画面不会跟着切。
        OnPropertyChanged(nameof(IsMonthView));
        OnPropertyChanged(nameof(IsWeekView));
        OnPropertyChanged(nameof(IsYearView));
        OnPropertyChanged(nameof(IsTaskView));
        OnPropertyChanged(nameof(IsMonthOrYearView));
        OnPropertyChanged(nameof(CurrentViewLabel));
        OnPropertyChanged(nameof(Title));

        // 切换视图时清除选中状态，让右侧面板回到今日任务
        ClearCellSelection();

        if (viewMode == CalendarViewMode.Month)
        {
            _timelineAnchor = new DateOnly(SelectedDate.Year, SelectedDate.Month, 1);
            BuildTimeline();
            RefreshHeightProperties();
        }
        else if (viewMode == CalendarViewMode.Tasks)
        {
            // 任务视图不渲染日历格子，只要保证两个任务列表是最新的即可
            // （它们在每次数据变更时本来就会刷新，这里补一次覆盖首次切入的场景）。
            RefreshTodayTasks();
            RefreshWeekTasks();
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
            // 任务视图没有翻页概念（顶栏也没有上一页/下一页按钮）
            CalendarViewMode.Tasks => SelectedDate,
            _ => SelectedDate.AddMonths(-1)
        };
    }

    public void MoveNext()
    {
        SelectedDate = Settings.ViewMode switch
        {
            CalendarViewMode.Year => SelectedDate.AddYears(1),
            CalendarViewMode.Week => SelectedDate.AddDays(7),
            CalendarViewMode.Tasks => SelectedDate,
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
        else if (Settings.ViewMode == CalendarViewMode.Week
                 && !dateChanged
                 && IndexOfTodayInWeekScroll >= 0)
        {
            // 周视图 + 选中的本来就是今天 + 今天仍在已铺开的列表里 → **数据一个字节都没变**，
            // 用户只是把左栏滚到几周之后去了，现在想滚回来。
            //
            // 这时若照常 RebuildCalendar()，就是 VisibleDays.Clear() 再新建 56 个
            // DayCellViewModel，每个都要重新挂任务、算徽标 —— 在 UI 线程上是实打实的
            // 整轮重排，用户感知就是「点了今天要卡 1~2 秒」。
            // 既然数据没变，重建毫无意义，只发滚动信号让宿主滚回今天那一行即可。
            TimelineRebuilt?.Invoke();
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
        // 任务视图不渲染任何日历格子：数据变更时只刷新两个任务列表即可，
        // 没必要去构建屏幕上根本不存在的月/周格子（任务多的时候能省掉整轮 UI VM 分配）。
        if (Settings.ViewMode == CalendarViewMode.Tasks)
        {
            RefreshTodayTasks();
            RefreshWeekTasks();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(TodayDisplay));
            OnPropertyChanged(nameof(TodayTaskCount));
            OnPropertyChanged(nameof(ThisWeekTaskCount));
            return;
        }

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
                ? CalendarService.BuildWeekScroll(SelectedDate, Today, WeekScrollInitialWeeks)
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

    /// <summary>
    /// 周视图向**前**插入日期格子（滚到接近顶部时由宿主调用）。
    ///
    /// <para><b>为什么以前看不了今天之前</b>：起始点固定在"本周周日"，而 <see cref="ExtendWeekScroll"/>
    /// 只往尾部 Add —— 今天之前的日期根本铺不出来，用户想回头看看前几天做不到。</para>
    ///
    /// <para>返回实际插入的天数（0 表示当前不是周视图，没插）。</para>
    /// </summary>
    public int ExtendWeekScrollBackward()
    {
        if (Settings.ViewMode != CalendarViewMode.Week || VisibleDays.Count == 0)
        {
            return 0;
        }

        var count = WeekScrollAppendWeeks * 7;
        var start = VisibleDays[0].Date.AddDays(-count);

        // 插到**头部**：其后每一行的索引都后移 count，宿主必须据此补偿滚动偏移
        // （见 WeekScrollHeadPrepended），否则画面会突然往下跳一整屏。
        var inserted = new List<DayCellViewModel>(count);
        for (var offset = 0; offset < count; offset++)
        {
            var date = start.AddDays(offset);
            inserted.Add(CreateDayCell(new CalendarDay(date, true, date == _today)));
        }

        for (var i = 0; i < inserted.Count; i++)
        {
            VisibleDays.Insert(i, inserted[i]);
        }

        WeekScrollHeadPrepended?.Invoke(count);

        // 与向后追加共用同一个总量上限。这次裁的是**尾部** —— 用户正在顶部附近，
        // 尾部是没在看的那一侧（向后追加时反之，裁头部）。
        var overflow = VisibleDays.Count - WeekScrollMaxDays;
        for (var i = 0; i < overflow; i++)
        {
            VisibleDays.RemoveAt(VisibleDays.Count - 1);
        }

        return count;
    }

    /// <summary>周视图滚到接近**顶部**时向头部插入了 N 天：宿主需要把滚动偏移加回 N 行。</summary>
    public event Action<int>? WeekScrollHeadPrepended;

    /// <summary>
    /// 周视图向后续追加日期格子（滚到接近底部时由宿主调用）。
    ///
    /// 追加点紧接在最后一个已有格子的次日，按整周推进 —— 因为左栏是"竖排日期"，
    /// 用户往下滚看到的是后面真实日期的连续序列，不能跳天。
    /// 返回实际追加的天数（0 表示当前不是周视图，没追加）。
    /// </summary>
    public int ExtendWeekScroll()
    {
        if (Settings.ViewMode != CalendarViewMode.Week || VisibleDays.Count == 0)
        {
            return 0;
        }

        var last = VisibleDays[^1].Date;
        // 下一个格子从"最后一天的次日"开始，保证日期连续不重复。
        var start = last.AddDays(1);
        var count = WeekScrollAppendWeeks * 7;

        for (var offset = 0; offset < count; offset++)
        {
            var date = start.AddDays(offset);
            VisibleDays.Add(CreateDayCell(new CalendarDay(date, true, date == _today)));
        }

        // 超出上限就从头部裁剪：裁掉的是用户早已滚过去的日期，不影响正在看的位置。
        // 但「今天」可能恰好在被裁掉的头部里 —— 那是用户往前滚了一百多天导致的，
        // IndexOfTodayInWeekScroll 会变成 -1，点「今天」自然退化走整段重建，行为依然正确。
        var overflow = VisibleDays.Count - WeekScrollMaxDays;
        if (overflow > 0)
        {
            for (var i = 0; i < overflow; i++)
            {
                VisibleDays.RemoveAt(0);
            }

            WeekScrollHeadTrimmed?.Invoke(overflow);
        }

        return count;
    }

    /// <summary>
    /// 周视图滚动列表的起点：**今天（或选中日）往前 <c>WeekCenterOffsetDays</c> 天**。
    ///
    /// <para>与 <see cref="CalendarService.BuildWeekScroll"/> 的起点保持同一个口径 ——
    /// 起点不再对齐到"本周周日"，是为了让今天落在第 4 行（上 3 下 3、今天居中）。</para>
    /// </summary>
    public DateOnly WeekScrollStartDate
    {
        get
        {
            var target = SelectedDate == default ? _today : SelectedDate;
            return target.AddDays(-Services.CalendarService.WeekCenterOffsetDays);
        }
    }

    /// <summary>
    /// "今天"在周视图左栏滚动列表里的行号；不在当前已铺开的范围内时返回 <c>-1</c>。
    ///
    /// 点「今天」要跳回今天所在这周 —— 周视图左栏是可无限上下滚的长列表，用户可能已经
    /// 翻到几周之后，此时"今天"未必在可见范围内。宿主拿这个索引决定滚到哪里。
    /// 返回 -1 表示还没铺到（理论上不会：列表从"本周周日"起铺，今天恒为第 0 行，
    /// 且只会往后追加），宿主可据此退化为"滚回顶部"。
    /// </summary>
    public int IndexOfTodayInWeekScroll
    {
        get
        {
            for (var i = 0; i < VisibleDays.Count; i++)
            {
                if (VisibleDays[i].Date == _today)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>以锚点月份为中心，构建前后各 2 个月的时间轴（共 5 个月）。</summary>
    private void BuildTimeline()    {
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
                tasks = OrderForDay(_data.Tasks.Where(task => task.Date == cell.Date)).ToList();
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
            tasks = OrderForDay(_data.Tasks.Where(task => task.Date == day.Date)).ToList();
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

    /// <summary>
    /// 同一天任务的统一显示顺序：<b>普通任务在前、周期任务整段垫底</b>；
    /// 两段内部再按「未完成在前 → 重要优先 → 创建时间」排。
    /// 日历格子与今日任务面板共用这一个口径，避免两处排序漂移。
    ///
    /// <para><b>为什么周期任务权重最小</b>：周期任务天天都在，是"背景噪声"；
    /// 用户真正要盯的是当天临时加的那几条。让周期任务整段沉到最下面，
    /// 一眼扫过去看到的就是"今天新出现的事"。这是用户明确要求的口径。</para>
    ///
    /// <para>放成**第一级**而不是最后一级：只加在末尾的话，它前面还隔着创建时间，
    /// 月初建的周期任务照样会排在当天新建的普通任务前面 —— 达不到"放在所有任务下面"。</para>
    /// </summary>
    private static IOrderedEnumerable<CalendarTask> OrderForDay(IEnumerable<CalendarTask> tasks)
        => tasks
            .OrderBy(task => task.IsRecurring)
            .ThenBy(task => task.IsCompleted)
            .ThenByDescending(task => task.IsImportant)
            .ThenBy(task => task.CreatedAt);
}
