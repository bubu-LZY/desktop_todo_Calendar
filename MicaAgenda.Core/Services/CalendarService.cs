using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

public static class CalendarService
{
    public static IReadOnlyList<CalendarDay> BuildMonth(DateOnly targetDate, DateOnly today)
    {
        var firstOfMonth = new DateOnly(targetDate.Year, targetDate.Month, 1);
        var gridStart = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);

        return Enumerable.Range(0, 42)
            .Select(offset =>
            {
                var date = gridStart.AddDays(offset);
                return new CalendarDay(
                    date,
                    date.Year == targetDate.Year && date.Month == targetDate.Month,
                    date == today);
            })
            .ToList();
    }

    public static IReadOnlyList<CalendarDay> BuildWeek(DateOnly targetDate, DateOnly today)
    {
        var weekStart = targetDate.AddDays(-(int)targetDate.DayOfWeek);

        return Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var date = weekStart.AddDays(offset);
                return new CalendarDay(date, true, date == today);
            })
            .ToList();
    }

    /// <summary>
    /// 周视图的可滚动日期序列：从"本周周日"起，连续铺 <paramref name="weeks"/> 周。
    ///
    /// 周视图左栏现在是一个可上下滚动的长列表 —— 默认停在开头（也就是最近 7 天），
    /// 往下滚就能看到后面日期的格子。所以这里返回的不是固定 7 天，而是一段更长的连续日期；
    /// 滚到接近底部时由 <c>MainViewModel.ExtendWeekScroll</c> 继续往后追加。
    ///
    /// 所有格子都标记为"本月内"（<c>IsInCurrentMonth = true</c>）：周视图本来就不按月分区，
    /// 若按真实月份判定，跨月的那几天会被淡化显示，看起来像"坏了"。
    /// </summary>
    public static IReadOnlyList<CalendarDay> BuildWeekScroll(DateOnly targetDate, DateOnly today, int weeks)
    {
        var weekStart = targetDate.AddDays(-(int)targetDate.DayOfWeek);
        var total = Math.Max(1, weeks) * 7;

        return Enumerable.Range(0, total)
            .Select(offset =>
            {
                var date = weekStart.AddDays(offset);
                return new CalendarDay(date, true, date == today);
            })
            .ToList();
    }
}
