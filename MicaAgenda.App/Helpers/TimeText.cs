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
}
