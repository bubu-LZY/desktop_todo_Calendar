namespace MicaAgenda.App.Models;

public sealed class CalendarTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public bool IsImportant { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// 状态最后变更时间：完成、取消完成都会刷新。
    /// 与 my-mindmap agent 同步复习计划时用它做时间戳仲裁——
    /// 只有 CompletedAt 是不够的，取消完成后 CompletedAt 会被清空，
    /// 无法判断"取消"这件事发生在对方"完成"之前还是之后。
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// 没给任务选时间时用的默认时刻（当天 9:00）。
    /// 「提前多久提醒」与「逾期未完成」都以任务时刻为锚点。
    /// </summary>
    public static readonly TimeOnly DefaultTime = new(9, 0);

    /// <summary>
    /// 任务当天的具体时间点（几点几分）。null = 用 <see cref="DefaultTime"/>（当天 9:00）。
    /// 添加任务时在日期右边选；不选就存 null，行为上等同当天 9:00 ——
    /// 这样与 my-mindmap agent 同步过来的复习任务（那边没有时间概念）保持同一份数据形态。
    /// </summary>
    public TimeOnly? Time { get; set; }

    /// <summary>
    /// 提前提醒量（分钟）：在「当天基准时刻 - 本值」推一次提醒。
    /// UI 上是单个下拉列表（提前 3 分钟 … 提前 3 个小时），直接存总分钟数。
    /// null = 不提醒：没选提前量就完全不推。
    /// </summary>
    public int? ReminderLeadMinutes { get; set; }

    /// <summary>
    /// 这条任务的一次性提醒已经推送过的时刻，null = 还没推。
    /// 30 秒轮询靠它去重，也是「程序重启后不会把当天早已到点的提醒重复推一遍」的依据。
    /// 改动时间 / 提前量后由 <see cref="ResetReminder"/> 清空。
    /// </summary>
    public DateTimeOffset? ReminderSentAt { get; set; }

    public void MarkCompleted(DateTimeOffset completedAt)
    {
        IsCompleted = true;
        CompletedAt = completedAt;
        UpdatedAt = completedAt;
    }

    public void MarkIncomplete()
    {
        IsCompleted = false;
        CompletedAt = null;
        UpdatedAt = DateTimeOffset.Now;
    }

    /// <summary>
    /// 同步回写：用对端给出的状态与时间戳更新本端，不覆盖为"当前时间"，
    /// 否则会破坏下一次仲裁（本地时间戳被刷新成 now，反而盖过了对端真实的变更时刻）。
    /// </summary>
    public void ApplySyncedStatus(bool completed, DateTimeOffset at)
    {
        IsCompleted = completed;
        CompletedAt = completed ? at : null;
        UpdatedAt = at;
    }

    /// <summary>是否是复习同步管辖的复习任务（标题带复习前缀）。用户自己建的任务不参与同步。</summary>
    public bool IsReviewTask => ReviewTaskTitle.IsReview(Title);

    /// <summary>创建日期（本地时区），用于"创建距今多久"一类展示。</summary>
    public DateOnly CreatedDate => DateOnly.FromDateTime(CreatedAt.LocalDateTime);

    // ===== 时间追踪：全部以「今天」为参照，所以做成方法而不是属性 =====

    /// <summary>
    /// 未完成天数：从创建那天算到 today 的自然日数。已完成的任务没有"未完成天数"，返回 0。
    /// 创建时间与 today 同一天时返回 0（当天建的当天还没做完，不算"拖了一天"）。
    /// </summary>
    public int GetPendingDays(DateOnly today)
    {
        if (IsCompleted)
        {
            return 0;
        }

        return Math.Max(0, today.DayNumber - CreatedDate.DayNumber);
    }

    /// <summary>
    /// 逾期天数：任务计划日期早于 today 且尚未完成时的落后天数，未逾期返回 0。
    /// 与"未完成天数"是两回事：8 月 1 日建、9 月 20 日到期的任务，未完成 50 天但不逾期。
    /// </summary>
    public int GetOverdueDays(DateOnly today)
    {
        if (IsCompleted || Date >= today)
        {
            return 0;
        }

        return today.DayNumber - Date.DayNumber;
    }

    /// <summary>是否处于逾期状态（未完成且已过计划日期）。</summary>
    public bool IsOverdue(DateOnly today) => GetOverdueDays(today) > 0;

    /// <summary>完成用时（完成时刻 - 创建时刻）。未完成或缺少时间戳时返回 null。</summary>
    public TimeSpan? GetCompletionDuration()
    {
        if (!IsCompleted || CompletedAt is null)
        {
            return null;
        }

        // 兜底：历史脏数据可能出现 CompletedAt < CreatedAt，钳到 0 避免出现负的"用时"
        var duration = CompletedAt.Value - CreatedAt;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>
    /// 完成时刻是否晚于计划日期当天，即"超时完成"。
    /// 例如计划 9/1、实际 9/3 完成 => true。当天完成 => false。
    /// </summary>
    public bool IsCompletedLate()
    {
        if (!IsCompleted || CompletedAt is null)
        {
            return false;
        }

        return DateOnly.FromDateTime(CompletedAt.Value.LocalDateTime) > Date;
    }

    /// <summary>超时完成逾期了多少天；非超时完成返回 0。</summary>
    public int GetCompletedLateDays()
    {
        if (!IsCompletedLate())
        {
            return 0;
        }

        return DateOnly.FromDateTime(CompletedAt!.Value.LocalDateTime).DayNumber - Date.DayNumber;
    }

    // ===== 到点提醒 =====

    /// <summary>
    /// 提醒触发时刻（当天基准时刻提前 <see cref="ReminderLeadMinutes"/> 分钟）；
    /// 没设提前量返回 null（= 这条任务不提醒）。
    /// </summary>
    public DateTime? ReminderTriggerAt()
        => ReminderLeadMinutes is not { } lead ? null : ScheduledAt.AddMinutes(-lead);

    /// <summary>
    /// 任务时刻 = 任务当天 + <see cref="Time"/>；没设时间就是当天 <see cref="DefaultTime"/>（9:00）。
    /// 「提前多久提醒」和「逾期未完成」都以它为准。
    /// </summary>
    public DateTime ScheduledAt => Date.ToDateTime(Time ?? DefaultTime);

    /// <summary>
    /// 此刻是否已经过了任务时刻（未完成才算逾期）。
    /// 没设时间的任务按当天 9:00 判定：今天 9:00 的任务，到 10:00 还没勾就会进「逾期未完成」。
    /// 右侧三组（逾期未完成 / 未完成 / 已完成）与批量删除、批量完成都用这个口径。
    /// </summary>
    public bool IsOverdueAt(DateTime nowLocal) => !IsCompleted && nowLocal >= ScheduledAt;

    /// <summary>当天补发窗口的右端（当天 23:59:59）：超过它就不再补推，避免开机时蹦出一堆陈旧提醒。</summary>
    private DateTime ReminderWindowEnd => Date.ToDateTime(new TimeOnly(23, 59, 59));

    /// <summary>此刻是否该推送这条任务的提醒（未完成、设了时间、没推过、已到点且仍在当天窗口内）。</summary>
    public bool ShouldFireReminder(DateTime nowLocal)
    {
        var trigger = ReminderTriggerAt();
        return !IsCompleted
               && ReminderSentAt is null
               && trigger is not null
               && nowLocal >= trigger.Value
               && nowLocal <= ReminderWindowEnd;
    }

    /// <summary>提醒已到点但超出当天窗口（隔天才开机）：应直接记成「已推」，不要补发。</summary>
    public bool IsReminderExpired(DateTime nowLocal)
    {
        var trigger = ReminderTriggerAt();
        return !IsCompleted
               && ReminderSentAt is null
               && trigger is not null
               && nowLocal > ReminderWindowEnd;
    }

    /// <summary>标记一次性提醒已推送。</summary>
    public void MarkReminderSent(DateTimeOffset at) => ReminderSentAt = at;

    /// <summary>改时间 / 改提前量后调用：原提醒作废，允许按新时刻重新推一次。</summary>
    public void ResetReminder() => ReminderSentAt = null;

    /// <summary>
    /// 落盘前的一致性修复：完成态与完成时间戳必须自洽，
    /// 否则报告里会出现"已完成但用时未知"这种算不出来的记录。
    /// </summary>
    public void Normalize(DateTimeOffset fallbackNow)
    {
        if (IsCompleted && CompletedAt is null)
        {
            CompletedAt = fallbackNow;
        }
        else if (!IsCompleted)
        {
            CompletedAt = null;
        }

        // 老数据没有 UpdatedAt：用完成时间兜底，未完成则用创建时间，
        // 保证同步仲裁时它不会因为没有时间戳而在比较中"永远最新"。
        if (UpdatedAt is null)
        {
            UpdatedAt = CompletedAt ?? CreatedAt;
        }

        // 提醒相关的自洽：没设提前量就没有提醒可言，顺手清掉遗留标记；
        // 负的提前量没有意义，钳到 0。
        if (ReminderLeadMinutes is null)
        {
            ReminderSentAt = null;
        }
        else if (ReminderLeadMinutes < 0)
        {
            ReminderLeadMinutes = 0;
        }
    }

    /// <summary>同步仲裁用的状态时间戳（必不为 null，Normalize 后保证有值）。</summary>
    public DateTimeOffset StatusTimestamp => UpdatedAt ?? CompletedAt ?? CreatedAt;
}
