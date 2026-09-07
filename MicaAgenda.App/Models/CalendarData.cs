namespace MicaAgenda.App.Models;

public sealed class CalendarData
{
    public CalendarSettings Settings { get; set; } = new();
    public List<CalendarTask> Tasks { get; set; } = [];
}
