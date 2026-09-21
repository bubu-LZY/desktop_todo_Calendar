using System.Text;

namespace MicaAgenda.App.Helpers;

/// <summary>
/// 时间跨度的中文短串格式化。ViewModel（悬浮提示）与 Services（报告）都要用，
/// 放在 Helpers 里避免 Services 反向依赖 ViewModels。
/// </summary>
public static class TimeText
{
    /// <summary>把时间跨度格式化成中文短串（"3分钟" / "2小时" / "3天7小时"）。</summary>
    public static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        if (value.TotalMinutes < 1)
        {
            return "不到 1 分钟";
        }

        if (value.TotalHours < 1)
        {
            return $"{value.Minutes}分钟";
        }

        if (value.TotalDays < 1)
        {
            return value.Minutes == 0
                ? $"{value.Hours}小时"
                : $"{value.Hours}小时{value.Minutes}分钟";
        }

        return value.Hours == 0
            ? $"{value.Days}天"
            : $"{value.Days}天{value.Hours}小时";
    }

    // ===== 「离到期还有多久 / 逾期多久」的唯一措辞 =====
    //
    // 用户给的口径（原话）："如果还没有到时间，就应该是'还有几天'；如果说已经到了，那就显示今天；
    // 如果说已经超期了，就应该显示已经逾期几天"。
    //
    // 关键在于**参照物是任务自己的日期**，不是创建时间。这两者很容易混：
    // 旧实现用 GetPendingDays()（从创建那天算起）写"3天未完"，
    // 于是一条「明天到期、三天前建」的任务被标成"3天未完" —— 用户看到就觉得奇怪，
    // 因为它既不是还有 3 天，也不是逾期 3 天，而是第三个毫无用处的时间量。
    //
    // 两个形态共用同一条规则，只是语域不同（徽标要短、提示/报告要成句）。
    // 规则本身只有下面这一个 switch —— 加新形态时**必须**复用它，别再手写第二个判断。

    /// <summary>
    /// 紧凑形态（用于任务行的小徽标）：<c>"还有 3 天"</c> / <c>"今天"</c> / <c>"已逾期 2 天"</c>。
    /// </summary>
    /// <param name="dueOffsetDays">
    /// 任务日期相对今天的偏移：<c>&gt;0</c> 已逾期几天、<c>&lt;0</c> 还有几天到期、<c>0</c> 今天到期。
    /// </param>
    public static string FormatDueOffset(int dueOffsetDays) => dueOffsetDays switch
    {
        > 0 => $"已逾期 {dueOffsetDays} 天",
        < 0 => $"还有 {-dueOffsetDays} 天",
        _ => "今天"
    };

    /// <summary>
    /// 成句形态（用于悬浮提示与报告正文）：<c>"还有 3 天到期"</c> / <c>"今天到期"</c> / <c>"已逾期 2 天"</c>。
    /// 与 <see cref="FormatDueOffset"/> 同一条规则，只是补了"到期"两个字，好在没有上下文的列表里读懂。
    /// </summary>
    public static string DescribeDueOffset(int dueOffsetDays) => dueOffsetDays switch
    {
        > 0 => $"已逾期 {dueOffsetDays} 天",
        < 0 => $"还有 {-dueOffsetDays} 天到期",
        _ => "今天到期"
    };

    /// <summary>
    /// 把「提前提醒量（分钟）」格式化成中文短串（"3天" / "2小时30分钟" / "15分钟"）。
    /// 提醒服务的推送文案与任务悬浮提示共用，避免两处各写一份、慢慢跑偏。
    /// </summary>
    public static string FormatLead(int totalMinutes)
    {
        if (totalMinutes <= 0)
        {
            return "0分钟";
        }

        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes % (24 * 60) / 60;
        var minutes = totalMinutes % 60;

        var sb = new StringBuilder();
        if (days > 0)
        {
            sb.Append(days).Append('天');
        }

        if (hours > 0)
        {
            sb.Append(hours).Append("小时");
        }

        if (minutes > 0)
        {
            sb.Append(minutes).Append("分钟");
        }

        return sb.ToString();
    }
}
