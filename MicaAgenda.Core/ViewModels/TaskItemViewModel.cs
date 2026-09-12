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

    public TaskItemViewModel(CalendarTask task, Func<DateTimeOffset>? nowProvider = null)
    {
        _task = task;
        _now = nowProvider ?? (() => DateTimeOffset.Now);
        _editTitle = task.Title;
        _shownIsCompleted = task.IsCompleted;
        _shownIsImportant = task.IsImportant;
        _shownTitle = task.Title;
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

    /// <summary>紧凑的时间徽标（如 "3天未完" / "逾期2天" / "用时2小时"），空间够的地方显示。</summary>
    public string TimeBadge
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
