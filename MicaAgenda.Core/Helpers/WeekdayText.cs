namespace MicaAgenda.App.Helpers;

/// <summary>
/// 「星期几」的中文短名，全项目唯一一份。
///
/// 原先这段 switch 在 <c>DayCellViewModel.WeekdayLabel</c>、<c>MainViewModel.TodayDisplay</c>
/// 各写了一份，月视图表头「日一二三四五六」又在 XAML 里硬编码了一份 —— 三处只要有一处
/// 改错，表现就是「格子日期和星期对不上」这种<b>静默错位</b>：不报错，但一眼就歪。
///
/// 周视图左栏改成竖排日期后，「首行显示周一」是高频误判点，所以这里把
/// <see cref="ShortNames"/> 的<b>顺序</b>也固化成一条断言依据：
/// 起点必须是周日（与 <c>CalendarService</c> 的 <c>AddDays(-(int)DayOfWeek)</c> 口径一致），
/// 任何一处想改成周一开头，都必须同步改这里 + 日历网格的铺法，改不掉就会被单测拦住。
/// </summary>
public static class WeekdayText
{
    /// <summary>
    /// 按「周日 → 周六」排列的短名（与 .NET <see cref="DayOfWeek"/> 的 0..6 一一对应）。
    /// 月视图 / 周视图的表头顺序直接用它，别再在 XAML 里手写一遍。
    /// </summary>
    public static IReadOnlyList<string> ShortNames { get; } =
        ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>月/周视图表头的单字形式（日 / 一 / 二 …），同样以周日开头。</summary>
    public static IReadOnlyList<string> HeaderChars { get; } =
        ["日", "一", "二", "三", "四", "五", "六"];

    /// <summary>把日期换成中文星期短名（"周一" … "周日"）。</summary>
    public static string Of(DateOnly date) => Of(date.DayOfWeek);

    /// <summary>把 <see cref="DayOfWeek"/> 换成中文星期短名（未知值兜底为"周日"）。</summary>
    public static string Of(DayOfWeek dayOfWeek)
    {
        var index = (int)dayOfWeek;
        return index >= 0 && index < ShortNames.Count ? ShortNames[index] : ShortNames[0];
    }
}
