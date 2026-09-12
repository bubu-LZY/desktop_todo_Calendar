namespace MicaAgenda.App.Models;

public sealed record CalendarDay(DateOnly Date, bool IsInCurrentMonth, bool IsToday);
