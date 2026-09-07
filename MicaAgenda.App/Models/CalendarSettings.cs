namespace MicaAgenda.App.Models;

public enum CalendarViewMode
{
    Year,
    Month,
    Week
}

public enum CalendarBackgroundMode
{
    Glass,
    Transparent,
    Solid,
    None,
    ClearBorder,
    FrostedWhite,
    FrostedGray,
    FrostedDark,
    AcrylicBlue,
    AcrylicMint,
    PaperLight,
    Graphite
}

public sealed class CalendarSettings
{
    public CalendarViewMode ViewMode { get; set; } = CalendarViewMode.Month;
    public CalendarBackgroundMode BackgroundMode { get; set; } = CalendarBackgroundMode.Glass;
    public double Opacity { get; set; } = 0.86;
    public double CellScale { get; set; } = 1.0;
    public double CellHeight { get; set; } = 74;
    /// <summary>
    /// 周视图日期格子的高度（独立于月视图，可单独拉伸）。
    /// 周视图三行都是 Auto，窗口高度由内容自适应，直接改窗口高度没有意义——
    /// 拖右下角手柄时改的是这个值，窗口再跟着内容自动变高/变矮。
    /// </summary>
    public double WeekCellHeight { get; set; } = 122;
    public bool AlwaysOnTop { get; set; }
    public bool AutoStart { get; set; }
    public bool HighPriorityStartup { get; set; }
    public WindowBounds WindowBounds { get; set; } = new(120, 90, 980, 680);
    public WindowBounds? MonthWindowBounds { get; set; }
    public WindowBounds? WeekWindowBounds { get; set; }
    public WindowBounds? YearWindowBounds { get; set; }
}
