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
    /// 周视图左栏的初始日期序列：**以 <paramref name="targetDate"/> 为中心**，向前后各铺一段。
    ///
    /// <para><b>为什么不再从"本周周日"起铺</b>：点「今天」时用户期望"今天在正中间" ——
    /// 上面 3 个格子、下面 3 个格子。从周日起铺的话，今天落在第 0~6 行取决于当天是星期几：
    /// 周日那天今天在最上面、周六那天在最下面，每点一次位置都不一样。</para>
    ///
    /// <para>向前铺满 <paramref name="daysBefore"/> 天是"今天居中"能成立的前提 ——
    /// 滚动位置归零时，今天正好是第 <c>daysBefore + 1</c> 行，上下各留够半个视口。</para>
    ///
    /// <para>所有格子都标记为"本月内"（<c>IsInCurrentMonth = true</c>）：周视图本来就不按月分区，
    /// 若按真实月份判定，跨月的那几天会被淡化显示，看起来像"坏了"。</para>
    /// </summary>
    public static IReadOnlyList<CalendarDay> BuildWeekScroll(
        DateOnly targetDate,
        DateOnly today,
        int weeks,
        int daysBefore = WeekCenterOffsetDays)
    {
        var start = targetDate.AddDays(-Math.Max(0, daysBefore));
        var total = Math.Max(1, weeks) * 7;

        return Enumerable.Range(0, total)
            .Select(offset =>
            {
                var date = start.AddDays(offset);
                return new CalendarDay(date, true, date == today);
            })
            .ToList();
    }

    /// <summary>
    /// "今天居中"时，今天上方要铺的天数。
    ///
    /// <para>取 3 是与"一屏 7 格"配套的：列表以今天为第 4 行起铺，滚动归零时
    /// 上面正好 3 格、今天居中、下面 3 格。想再往前看就靠向上滚动继续铺
    /// （<c>MainViewModel.ExtendWeekScrollBackward</c>）。</para>
    /// </summary>
    public const int WeekCenterOffsetDays = 3;
}
