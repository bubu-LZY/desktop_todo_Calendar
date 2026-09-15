using System.Text;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.ViewModels;

public sealed class TaskItemViewModel : ViewModelBase
{
    private readonly CalendarTask _task;
    private readonly Func<DateTimeOffset> _now;
    private bool _isEditing;
    private string _editTitle;

    // ===== UI 状态快照 =====
    // 底层 CalendarTask 是 POCO、不实现 INotifyPropertyChanged，外部改了 model 之后
    // 本 VM 完全不知情。Snapshot 记录"上一次推给 UI 的值"，只有快照与 model 不一致时才
    // 发 PropertyChanged —— 这样既能保证 UI 拿到最新状态，又不会每轮刷新都无谓地
    // 触发 DataTrigger / 重排分组（历史上无条件发通知会让右侧面板反复重建、闪动）。
    // 同时它也是结构 diff 的依据：DayCellViewModel 靠它判断"要不要重排任务顺序"。
    private bool _shownIsCompleted;
    private bool _shownIsImportant;
    private string _shownTitle;
    private TimeOnly? _shownTime;
    private int? _shownLeadMinutes;
    private int _orderIndex;

    public TaskItemViewModel(CalendarTask task, Func<DateTimeOffset>? nowProvider = null)
    {
        _task = task;
        _now = nowProvider ?? (() => DateTimeOffset.Now);
        _editTitle = task.Title;
        _shownIsCompleted = task.IsCompleted;
        _shownIsImportant = task.IsImportant;
        _shownTitle = task.Title;
        _shownTime = task.Time;
        _shownLeadMinutes = task.ReminderLeadMinutes;
    }

    public Guid Id => _task.Id;
    public DateOnly Date => _task.Date;
    public CalendarTask Model => _task;

    /// <summary>创建时间（底层 model 直通）。</summary>
    public DateTimeOffset CreatedAt => _task.CreatedAt;

    /// <summary>完成时间；未完成为 null。</summary>
    public DateTimeOffset? CompletedAt => _task.CompletedAt;

    public string Title
    {
        get => _task.Title;
        set
        {
            if (_task.Title == value)
            {
                return;
            }

            _task.Title = value;
            _shownTitle = value;
            OnPropertyChanged();
        }
    }

    /// <summary>读底层 <see cref="CalendarTask.IsCompleted"/>。</summary>
    public bool IsCompleted => _task.IsCompleted;

    /// <summary>读底层 <see cref="CalendarTask.IsImportant"/>。</summary>
    public bool IsImportant => _task.IsImportant;

    // ===== 快照：给 DayCellViewModel / MainViewModel 做结构 diff 用 =====

    /// <summary>UI 当前显示的完成态。</summary>
    public bool ShownIsCompleted => _shownIsCompleted;

    /// <summary>UI 当前显示的重要态。</summary>
    public bool ShownIsImportant => _shownIsImportant;

    /// <summary>UI 当前显示的标题。</summary>
    public string ShownTitle => _shownTitle;

    /// <summary>
    /// 在所属列表里的序号（1 起）。由持有它的列表（<see cref="DayCellViewModel"/>）在
    /// 构建 / 重排之后统一写入，UI 用它渲染任务前面的「1.」「2.」。
    /// 0 表示未编号（不分组的列表里不显示序号）。
    /// </summary>
    public int OrderIndex
    {
        get => _orderIndex;
        set
        {
            if (!SetProperty(ref _orderIndex, value))
            {
                return;
            }

            OnPropertyChanged(nameof(OrderText));
        }
    }

    /// <summary>序号文本（"1." / "2."）；未编号时是空串。</summary>
    public string OrderText => _orderIndex > 0 ? $"{_orderIndex}." : string.Empty;

    /// <summary>提醒时刻文本（"08:30"，= 基准时刻减掉提前量）；没设提醒是空串。</summary>
    public string ReminderTimeText
        => _task.ReminderTriggerAt() is { } trigger ? trigger.ToString("HH:mm") : string.Empty;

    /// <summary>是否设了提前提醒。UI 用它在标题前面留出提醒小字的位置。</summary>
    public bool HasReminder => _task.ReminderLeadMinutes is not null;

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public string EditTitle
    {
        get => _editTitle;
        set => SetProperty(ref _editTitle, value);
    }

    public void BeginEdit()
    {
        EditTitle = Title;
        IsEditing = true;
    }

    public void CancelEdit()
    {
        EditTitle = Title;
        IsEditing = false;
    }

    /// <summary>
    /// 把底层 model 的最新状态同步到 UI。
    ///
    /// 这一步是左右侧同步的关键：日历格子、今日任务面板、本周分组各自持有
    /// 独立的 TaskItemViewModel 实例（同一条任务会有 3~4 个 VM）。在任意一侧勾选完成，
    /// 改的是同一个 CalendarTask 对象，但其余 VM 不会收到任何通知 —— 必须由调用方
    /// 在刷新时对每个 VM 调一次本方法，把差异推给各自的绑定目标。
    ///
    /// 只有快照与 model 真的不一致时才发 PropertyChanged，重复调用开销可忽略。
    /// </summary>
    public void SyncFromModel()
    {
        if (_shownIsCompleted != _task.IsCompleted)
        {
            _shownIsCompleted = _task.IsCompleted;
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(TooltipText));
            OnPropertyChanged(nameof(TimeBadge));
        }

        if (_shownIsImportant != _task.IsImportant)
        {
            _shownIsImportant = _task.IsImportant;
            OnPropertyChanged(nameof(IsImportant));
        }

        if (!string.Equals(_shownTitle, _task.Title, StringComparison.Ordinal))
        {
            _shownTitle = _task.Title;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(TooltipText));
        }

        if (_shownTime != _task.Time || _shownLeadMinutes != _task.ReminderLeadMinutes)
        {
            _shownTime = _task.Time;
            _shownLeadMinutes = _task.ReminderLeadMinutes;
            OnPropertyChanged(nameof(ReminderTimeText));
            OnPropertyChanged(nameof(HasReminder));
            OnPropertyChanged(nameof(TimeBadge));
            OnPropertyChanged(nameof(TooltipText));
        }
    }

    /// <summary>
    /// 无条件重推全部展示属性（含与时间相关的派生文本）。
    /// 每当时钟 tick / 跨天时调用，让"未完成 N 天"这类文本跟着走。
    /// </summary>
    public void RefreshDerived()
    {
        SyncFromModel();
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(TimeBadge));
    }

    // ===== 时间信息展示 =====

    private DateOnly Today => DateOnly.FromDateTime(_now().LocalDateTime);

    /// <summary>
    /// 紧凑的提醒徽标（如 "08:30 提醒 · 3天未完" / "逾期2天" / "用时2小时"）。
    /// 设了提前提醒的任务把提醒时刻顶在最前面：右侧面板里一眼就能看出几点会响。
    /// </summary>
    public string TimeBadge
    {
        get
        {
            var status = StatusBadge;
            return HasReminder ? $"{ReminderTimeText} 提醒 · {status}" : status;
        }
    }

    /// <summary>状态徽标本体（与"有没有设时间"无关）。</summary>
    private string StatusBadge
    {
        get
        {
            var today = Today;
            if (IsCompleted)
            {
                var duration = _task.GetCompletionDuration();
                return duration is null ? "已完成" : $"用时{FormatDuration(duration.Value)}";
            }

            var overdue = _task.GetOverdueDays(today);
            if (overdue > 0)
            {
                return $"逾期{overdue}天";
            }

            var pending = _task.GetPendingDays(today);
            return pending > 0 ? $"{pending}天未完" : "今天";
        }
    }

    /// <summary>悬浮提示全文：标题 + 创建时间 + 完成时间 + 未完成/逾期/用时等。</summary>
    public string TooltipText
    {
        get
        {
            var today = Today;
            var sb = new StringBuilder();
            sb.AppendLine(Title);

            var createdDay = _task.CreatedDate;
            if (_task.ReminderLeadMinutes is { } lead)
            {
                var anchor = _task.Time ?? CalendarTask.DefaultTime;
                // 提前量 0 就是「到时提醒」：不提前，任务时刻那一刻推
                var leadText = lead > 0 ? $"提前 {Helpers.TimeText.FormatLead(lead)}" : "到时提醒";
                sb.AppendLine(
                    $"提醒：{_task.ReminderTriggerAt():HH:mm}（{leadText}，任务时刻 {anchor:HH:mm}）");
            }

            sb.Append($"创建：{_task.CreatedAt:MM/dd HH:mm}");
            if (createdDay == today)
            {
                sb.Append("（今天）");
            }
            else
            {
                sb.Append($"（{createdDay:MM/dd}）");
            }
            sb.AppendLine();

            if (IsCompleted)
            {
                if (_task.CompletedAt is { } completedAt)
                {
                    sb.AppendLine($"完成：{completedAt:MM/dd HH:mm}");
                }

                var duration = _task.GetCompletionDuration();
                sb.Append(duration is null ? "用时：未知" : $"用时：{FormatDuration(duration.Value)}");

                var lateDays = _task.GetCompletedLateDays();
                if (lateDays > 0)
                {
                    sb.Append($" · 超时 {lateDays} 天完成");
                }

                return sb.ToString();
            }

            var pending = _task.GetPendingDays(today);
            sb.Append(pending > 0 ? $"未完成 {pending} 天" : "当天创建，尚未完成");

            var overdue = _task.GetOverdueDays(today);
            if (overdue > 0)
            {
                sb.Append($" · 已逾期 {overdue} 天");
            }

            return sb.ToString();
        }
    }

    /// <summary>把时间跨度格式化成中文短串（"3分钟" / "2小时" / "3天7小时"）。</summary>
    private static string FormatDuration(TimeSpan value) => Helpers.TimeText.FormatDuration(value);
}
