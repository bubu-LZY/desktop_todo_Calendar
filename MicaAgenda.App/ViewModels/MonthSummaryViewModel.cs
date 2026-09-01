namespace MicaAgenda.App.ViewModels;

public sealed class MonthSummaryViewModel
{
    public MonthSummaryViewModel(int month, IReadOnlyList<DayCellViewModel> days)
    {
        Month = month;
        Days = days;
    }

    public int Month { get; }
    public string Title => $"{Month}月";
    public IReadOnlyList<DayCellViewModel> Days { get; }
    public int TaskCount => Days.Sum(day => day.Tasks.Count);
    public int CompletedCount => Days.Sum(day => day.CompletedCount);
}
