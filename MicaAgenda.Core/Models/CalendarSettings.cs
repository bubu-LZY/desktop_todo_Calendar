namespace MicaAgenda.App.Models;

public enum CalendarViewMode
{
    Year,
    Month,
    Week
}

public enum CalendarBackgroundMode
{
    // ⚠️ Glass / Transparent / Solid 是已下线的历史主题：v4.0.0 起下拉列表里不再出现，
    // 加载老配置时由 CalendarBackgroundModeExtensions.MigrateLegacy() 统一迁移到 FrostedWhite。
    // 枚举值本身必须保留 —— 老 JSON 里写着 "Glass" 时，System.Text.Json 遇到未知枚举名会抛
    // JsonException，而 CalendarDataStore 一旦解析失败就退化成「空数据」，等于把用户任务全丢掉。
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

public static class CalendarBackgroundModeExtensions
{
    /// <summary>
    /// 把已下线的历史主题迁到现役主题。
    ///
    /// 毛玻璃 / 透明 / 纯色在 v3.3.x 里实际都是「半透明白底」（窗口不再请求系统材质之后，
    /// 三者走的是同一个分支），现役主题里观感最接近的是白雾玻璃，因此统一迁到它，
    /// 用户升级后看到的差异最小。ClearBorder 是更早的历史值，继续迁到「无背景」。
    /// </summary>
    public static CalendarBackgroundMode MigrateLegacy(this CalendarBackgroundMode mode) => mode switch
    {
        CalendarBackgroundMode.Glass
            or CalendarBackgroundMode.Transparent
            or CalendarBackgroundMode.Solid => CalendarBackgroundMode.FrostedWhite,
        CalendarBackgroundMode.ClearBorder => CalendarBackgroundMode.None,
        _ => mode
    };
}

public sealed class CalendarSettings
{
    public CalendarViewMode ViewMode { get; set; } = CalendarViewMode.Month;
    public CalendarBackgroundMode BackgroundMode { get; set; } = CalendarBackgroundMode.FrostedWhite;
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
