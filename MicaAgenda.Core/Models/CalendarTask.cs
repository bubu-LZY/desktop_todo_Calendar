using System.Text.Json.Serialization;

namespace MicaAgenda.App.Models;

/// <summary>
/// 周期任务的重复频率。
/// </summary>
public enum RecurrenceFrequency
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Yearly = 4,
}

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
    /// 重复频率：None = 普通一次性任务；其余表示这是一条「周期任务」的<b>源任务</b>（系列根）。
    /// 源任务自己就是第一次发生；后续发生的实例由 <see cref="SeriesId"/> 指向它。
    /// </summary>
    public RecurrenceFrequency Recurrence { get; set; } = RecurrenceFrequency.None;

    /// <summary>重复间隔：每 N 天 / 周 / 月 / 年一次（最小 1）。</summary>
    public int RecurrenceInterval { get; set; } = 1;

    /// <summary>重复结束日期（含）。null = 不设结束，物化时封顶到约 2 年。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? RecurrenceEnd { get; set; }

    /// <summary>
    /// 物化出来的重复实例指向源任务（系列根）的 Id；null 表示这不是周期任务的实例。
    /// 源任务本身 <c>Recurrence != None</c> 且 <c>SeriesId == null</c>。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SeriesId { get; set; }

    /// <summary>是否是周期任务的源任务（系列根）。</summary>
    [JsonIgnore]
    public bool IsRecurringMaster => Recurrence != RecurrenceFrequency.None;

    /// <summary>是否是由周期任务物化出来的重复实例。</summary>
    [JsonIgnore]
    public bool IsRecurringInstance => SeriesId is not null;

    /// <summary>
    /// 是否属于周期任务 —— 源任务与其实例都算。
    ///
    /// <para><b>必须两个都判</b>：源任务自己也是一个可见任务，而实例的 <see cref="Recurrence"/> 是
    /// <c>None</c>（规则只存在源任务上）。只判其中一个都会漏掉一半，UI 上就会出现
    /// "有的周期任务有【周期】标识、有的没有"。</para>
    ///
    /// <para>同时它也是排序的最后一级权重：周期任务排在所有任务之后（见
    /// <c>MainViewModel.OrderForDay</c>）。</para>
    /// </summary>
    [JsonIgnore]
    public bool IsRecurring => IsRecurringMaster || IsRecurringInstance;

    /// <summary>
    /// 主提醒的提前量（分钟）：在「当天基准时刻 - 本值」推一次提醒。
    /// 多选提醒时它是第一个勾选项；其余档位在 <see cref="AdditionalReminderLeadMinutes"/>。
    /// null = 一个提醒都没设：完全不推。
    /// 历史数据只有这一个字段，保留它也是为了旧版本 / API / WPF 宿主读得懂。
    /// </summary>
    public int? ReminderLeadMinutes { get; set; }

    /// <summary>
    /// 主提醒之外的其他提醒提前量（分钟），多选下拉里除第一个勾选项外的档位都在这里。
    /// 为 null / 空表示没有额外提醒；落盘时空列表不写出。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? AdditionalReminderLeadMinutes { get; set; }

    /// <summary>
    /// 主提醒已经推送过的时刻，null = 还没推。
    /// 30 秒轮询靠它去重，也是「程序重启后不会把当天早已到点的提醒重复推一遍」的依据。
    /// 改动时间 / 提前量后由 <see cref="ResetReminder"/> 清空。
    /// </summary>
    public DateTimeOffset? ReminderSentAt { get; set; }

    /// <summary>
    /// 额外提醒（<see cref="AdditionalReminderLeadMinutes"/>）里已经推送过的提前量。
    /// 主提醒的去重走 <see cref="ReminderSentAt"/>；这里只记额外档位，各推一次、互不挡。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? FiredReminderLeads { get; set; }

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
    /// 任务日期相对 <paramref name="today"/> 的偏移：<c>&gt;0</c> 已逾期几天、
    /// <c>&lt;0</c> 还有几天到期、<c>0</c> 今天到期。
    ///
    /// <para><b>这才是界面上"还有几天 / 今天 / 已逾期几天"该用的口径</b> ——
    /// 参照物是<b>任务自己的日期</b>，不是创建时间。措辞统一由
    /// <see cref="Helpers.TimeText.FormatDueOffset"/> / <c>DescribeDueOffset</c> 生成。</para>
    ///
    /// <para>与 <see cref="GetOverdueDays"/> 的区别只有一个：那个会把"今天到期"也算成 0，
    /// 并且已完成的任务恒为 0；而这里不管完成与否，只回答"日期离今天多远"，
    /// 由调用方决定要不要显示（已完成的任务显示的是用时，不是这个）。</para>
    ///
    /// <para>逾期的界线与提醒一致：<b>跨过任务当日的 24:00 才算逾期</b>，
    /// 所以"今天到期但时刻已过"仍然是「今天」。</para>
    /// </summary>
    public int GetDueOffsetDays(DateOnly today) => today.DayNumber - Date.DayNumber;

    /// <summary>
    /// 逾期天数：任务计划日期早于 today 且尚未完成时的落后天数，未逾期返回 0。
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

    // ===== 到点提醒（支持多选：每个勾选项各推一次）=====

    /// <summary>
    /// 合法提前量上界（7 天）。档位表里最大只有「提前一天」(1440)，
    /// 这里留到 7 天只是防御手改 JSON：过大的值会让 <see cref="ScheduledAt.AddMinutes"/> 抛
    /// ArgumentOutOfRangeException，把每 30 秒一次的整轮轮询全部打挂。
    /// </summary>
    public const int MaxLeadMinutes = 7 * 24 * 60;

    /// <summary>
    /// 这条任务设置的全部提醒提前量：主提醒在前，去重、非负、不超过 <see cref="MaxLeadMinutes"/>。空列表 = 不提醒。
    /// 负的提前量没有意义，读的时候统一钳到 0（=「到时提醒」）。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<int> AllReminderLeads
    {
        get
        {
            var result = new List<int>();
            if (ReminderLeadMinutes is { } primary)
            {
                result.Add(ClampLead(primary));
            }

            if (AdditionalReminderLeadMinutes is { } extra)
            {
                foreach (var lead in extra)
                {
                    var value = ClampLead(lead);
                    if (!result.Contains(value))
                    {
                        result.Add(value);
                    }
                }
            }

            return result;
        }
    }

    /// <summary>把单个提前量钳进 [0, <see cref="MaxLeadMinutes"/>]。</summary>
    private static int ClampLead(int lead) => Math.Clamp(lead, 0, MaxLeadMinutes);

    /// <summary>是否设了至少一个提醒。</summary>
    [JsonIgnore]
    public bool HasReminders => AllReminderLeads.Count > 0;

    /// <summary>
    /// 一次性写回全部提醒档位（多选下拉保存时用）：第一个成为主提醒（<see cref="ReminderLeadMinutes"/>），
    /// 其余进 <see cref="AdditionalReminderLeadMinutes"/>；空集合 = 不提醒（两处都清空）。
    /// </summary>
    public void SetReminderLeads(IEnumerable<int>? leads)
    {
        var normalized = (leads ?? [])
            .Select(ClampLead)
            .Distinct()
            .ToList();

        ReminderLeadMinutes = normalized.Count > 0 ? normalized[0] : null;
        AdditionalReminderLeadMinutes = normalized.Count > 1 ? normalized.Skip(1).ToList() : null;
    }

    /// <summary>
    /// 主提醒触发时刻（当天基准时刻提前 <see cref="ReminderLeadMinutes"/> 分钟）；
    /// 没设提前量返回 null（= 这条任务不提醒）。
    /// 多选时它只是第一个勾选项的时刻；全部时刻见 <see cref="ReminderTriggers"/>。
    /// </summary>
    public DateTime? ReminderTriggerAt()
        => ReminderLeadMinutes is not { } lead ? null : ScheduledAt.AddMinutes(-ClampLead(lead));

    /// <summary>指定档位的触发时刻（任务时刻往前推 lead 分钟）。</summary>
    public DateTime ReminderTriggerAt(int lead) => ScheduledAt.AddMinutes(-ClampLead(lead));

    /// <summary>全部提醒档位各自的触发时刻（提前量, 时刻），按勾选顺序。</summary>
    public IEnumerable<(int Lead, DateTime TriggerAt)> ReminderTriggers()
        => AllReminderLeads.Select(lead => (lead, ScheduledAt.AddMinutes(-lead)));

    /// <summary>最早的一次提醒时刻（任务条小字只展示一个时间时用它）；没设提醒为 null。</summary>
    public DateTime? EarliestReminderTriggerAt()
    {
        var triggers = ReminderTriggers().Select(item => item.TriggerAt).ToList();
        return triggers.Count == 0 ? null : triggers.Min();
    }

    /// <summary>这个档位的提醒是否已经推送过（主提醒看 <see cref="ReminderSentAt"/>，额外档看 <see cref="FiredReminderLeads"/>）。</summary>
    public bool IsReminderFired(int lead)
    {
        var leads = AllReminderLeads;
        if (leads.Count > 0 && lead == leads[0])
        {
            return ReminderSentAt is not null;
        }

        return FiredReminderLeads?.Contains(lead) == true;
    }

    /// <summary>记下某个档位的提醒已推送。</summary>
    public void MarkReminderSent(int lead, DateTimeOffset at)
    {
        var leads = AllReminderLeads;
        if (leads.Count > 0 && lead == leads[0])
        {
            ReminderSentAt = at;
            return;
        }

        FiredReminderLeads ??= [];
        if (!FiredReminderLeads.Contains(lead))
        {
            FiredReminderLeads.Add(lead);
        }
    }

    /// <summary>
    /// 新建 / 编辑保存时调用：把「触发时刻已过、且已经超出该档位补发窗口」的档位直接记为已推，
    /// 避免保存一条任务就立刻蹦出陈旧提醒（典型：给今天的任务勾「提前一天」）。
    /// 仍在补发窗口内（如小档位当天刚过点几分钟）的档位不动，保留开机/新建后的补发机会。
    /// </summary>
    public void SuppressMissedLeadReminders(DateTimeOffset now)
    {
        var nowLocal = now.LocalDateTime;
        foreach (var (lead, trigger) in ReminderTriggers())
        {
            var catchUpEnd = lead >= LongLeadThresholdMinutes
                ? trigger.AddMinutes(LongLeadCatchUpGraceMinutes)
                : ReminderWindowEnd;
            if (nowLocal > catchUpEnd && !IsReminderFired(lead))
            {
                MarkReminderSent(lead, now);
            }
        }
    }

    /// <summary>
    /// 「提前一天」这类跨天长档位的补发宽限：触发后 60 分钟内开机/轮询仍补发，超过就不再补。
    /// 小档位（到时 / 提前几十分钟）沿用任务当天 23:59:59 的补发窗口——当天开机补发是刻意保留的。
    /// 不给长档位限宽限的话，任务日当天早上开机还会收到一条「还有 1 天」的误导提醒（实际几小时后就到点）。
    /// </summary>
    public const int LongLeadCatchUpGraceMinutes = 60;

    /// <summary>提前量达到「提前一天」即视为跨天长档位，补发走短宽限而不是当天窗口。</summary>
    public const int LongLeadThresholdMinutes = 24 * 60;

    /// <summary>
    /// 此刻到点、还没推、仍在补发窗口内的全部档位（可能一次有多个，例如「提前一天」和「到时提醒」同一次轮询都到期）。
    /// </summary>
    public List<int> DueReminderLeads(DateTime nowLocal)
    {
        if (IsCompleted)
        {
            return [];
        }

        var due = new List<int>();
        foreach (var (lead, trigger) in ReminderTriggers())
        {
            // 长档位：只在触发后一小段宽限内补发；小档位：任务当天结束前都允许补发
            var windowEnd = lead >= LongLeadThresholdMinutes
                ? trigger.AddMinutes(LongLeadCatchUpGraceMinutes)
                : ReminderWindowEnd;
            if (!IsReminderFired(lead) && nowLocal >= trigger && nowLocal <= windowEnd)
            {
                due.Add(lead);
            }
        }

        return due;
    }

    /// <summary>已到点但超出当天窗口（隔天才开机）、还没推的档位：应直接记成「已推」，不要补发。</summary>
    public List<int> ExpiredReminderLeads(DateTime nowLocal)
    {
        if (IsCompleted)
        {
            return [];
        }

        var expired = new List<int>();
        foreach (var (lead, trigger) in ReminderTriggers())
        {
            if (!IsReminderFired(lead) && nowLocal > ReminderWindowEnd && trigger <= ReminderWindowEnd)
            {
                expired.Add(lead);
            }
        }

        return expired;
    }

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

    /// <summary>改时间 / 改提醒档位后调用：全部档位的「已推」标记作废，允许按新时刻重新推。</summary>
    public void ResetReminder()
    {
        ReminderSentAt = null;
        FiredReminderLeads = null;
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

        // 提醒相关的自洽：没设主提前量时额外档位也不成立，顺手清掉遗留标记；
        // 负的提前量没有意义，统一钳到 0；已推标记里若混进了当前档位之外的脏值也一并清掉。
        if (ReminderLeadMinutes is null)
        {
            ReminderSentAt = null;
            AdditionalReminderLeadMinutes = null;
            FiredReminderLeads = null;
        }
        else
        {
            if (ReminderLeadMinutes < 0)
            {
                ReminderLeadMinutes = 0;
            }

            if (AdditionalReminderLeadMinutes is { } extra && extra.Count > 0)
            {
                AdditionalReminderLeadMinutes = extra
                    .Select(lead => lead < 0 ? 0 : lead)
                    .Where(lead => lead != ReminderLeadMinutes.Value)
                    .Distinct()
                    .ToList();
                if (AdditionalReminderLeadMinutes.Count == 0)
                {
                    AdditionalReminderLeadMinutes = null;
                }
            }
            else
            {
                AdditionalReminderLeadMinutes = null;
            }

            if (FiredReminderLeads is { } fired && fired.Count > 0)
            {
                var validLeads = AllReminderLeads.Skip(1).ToHashSet();
                FiredReminderLeads = fired.Where(validLeads.Contains).Distinct().ToList();
                if (FiredReminderLeads.Count == 0)
                {
                    FiredReminderLeads = null;
                }
            }
        }

        // 周期任务字段自洽：非重复任务不该残留重复规则；间隔最小为 1。
        if (Recurrence == RecurrenceFrequency.None)
        {
            RecurrenceEnd = null;
            RecurrenceInterval = 1;
        }
        else if (RecurrenceInterval < 1)
        {
            RecurrenceInterval = 1;
        }
    }

    /// <summary>同步仲裁用的状态时间戳（必不为 null，Normalize 后保证有值）。</summary>
    public DateTimeOffset StatusTimestamp => UpdatedAt ?? CompletedAt ?? CreatedAt;
}
