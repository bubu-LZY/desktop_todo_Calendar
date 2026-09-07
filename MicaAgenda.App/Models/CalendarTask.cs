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
    }

    /// <summary>同步仲裁用的状态时间戳（必不为 null，Normalize 后保证有值）。</summary>
    public DateTimeOffset StatusTimestamp => UpdatedAt ?? CompletedAt ?? CreatedAt;
}
