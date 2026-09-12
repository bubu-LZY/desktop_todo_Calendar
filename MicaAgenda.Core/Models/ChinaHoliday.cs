namespace MicaAgenda.App.Models;

public sealed class ChinaHoliday
{
    public DateOnly Date { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsHoliday { get; set; }
    public bool IsMakeupWorkday { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string BadgeText => IsMakeupWorkday ? $"班 {Name}" : $"休 {Name}";
    public string YearBadgeText => IsMakeupWorkday ? "班" : "休";
}
