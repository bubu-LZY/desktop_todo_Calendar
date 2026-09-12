using System.Collections.ObjectModel;

namespace MicaAgenda.App.ViewModels;

/// <summary>
/// 时间轴视图中的一个月份块：标题 + 该月所有日期格子。
/// </summary>
public sealed class MonthBlockViewModel : ViewModelBase
{
    public int Year { get; }

    public int Month { get; }

    public string Title { get; }

    public ObservableCollection<DayCellViewModel> Days { get; }

    public bool ContainsToday { get; }

    public MonthBlockViewModel(int year, int month, IEnumerable<DayCellViewModel> days, bool containsToday)
    {
        Year = year;
        Month = month;
        Title = $"{year}年{month}月";
        Days = new ObservableCollection<DayCellViewModel>(days);
        ContainsToday = containsToday;
    }
}
