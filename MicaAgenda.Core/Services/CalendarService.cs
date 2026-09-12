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
}
