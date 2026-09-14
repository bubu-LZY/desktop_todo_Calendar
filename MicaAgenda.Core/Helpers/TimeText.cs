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
