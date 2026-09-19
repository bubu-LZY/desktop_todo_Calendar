namespace MicaAgenda.App.Models;

public sealed record WindowBounds(double Left, double Top, double Width, double Height);

/// <summary>
/// 「启动恢复窗口几何」的取值规则。
///
/// <para>抽成纯函数放在 Core，是因为 Avalonia 宿主（MicaAgenda.Desktop）和 WPF 宿主（MicaAgenda.App）
/// 各写了一份实现 —— 两边曾经一起踩同一个坑（见下），规则只能有一处定义。</para>
/// </summary>
public static class WindowBoundsResolver
{
    /// <summary>
    /// 算出启动时该把窗口摆成什么样。
    ///
    /// <para><b>不变式：位置只有一个来源 —— 通用记忆 <see cref="CalendarSettings.WindowBounds"/>;
    /// 分视图记忆（Month / Week / Year）只贡献该视图上次的<b>尺寸</b>。</b></para>
    ///
    /// <para>位置为什么不分视图记：切视图时窗口位置本来就保持不变（只换尺寸），
    /// 分记反而会让窗口在屏幕上乱跳。既然位置全局唯一，就不该在分视图条目里再存一份 ——
    /// 两份数据迟早会打架。</para>
    ///
    /// <para><b>这个坑真实发生过</b>：早先的写入侧把分视图条目的 Left/Top 抹成 0，
    /// 表达"分视图只记尺寸"；而读取侧却把整条记录当完整边界用，于是每次重启窗口都精准落在
    /// 屏幕左上角（用户反馈"重启后记不住位置，默认打开就在左上角"）。
    /// 现在位置统一取自通用记忆，顺带把<b>老配置里残留的 0 坐标自动修正</b>——
    /// 分视图条目的 Left/Top 根本不参与恢复，用户不需要清配置。</para>
    /// </summary>
    /// <param name="settings">当前设置。</param>
    /// <param name="mode">启动时使用的视图。</param>
    /// <param name="fallback">
    /// 兜底边界：当通用记忆和分视图记忆都不可用时使用。由宿主提供
    /// （通常是「当前窗口位置 + 该视图的默认尺寸」）。
    /// </param>
    public static WindowBounds ForStartup(
        CalendarSettings settings,
        CalendarViewMode mode,
        WindowBounds fallback)
    {
        // 通用记忆是位置的唯一来源。它正常情况下非空且带默认值 (120,90,980,680)，
        // 只有 JSON 里被写成 null / 宽高为非正数时才退回 fallback。
        var global = IsUsable(settings.WindowBounds) ? settings.WindowBounds : fallback;

        var perView = mode switch
        {
            CalendarViewMode.Month => settings.MonthWindowBounds,
            CalendarViewMode.Week => settings.WeekWindowBounds,
            CalendarViewMode.Year => settings.YearWindowBounds,
            _ => null
        };

        // 尺寸优先用该视图自己的记忆（老数据 / 从没切过该视图时为 null）→ 退回通用记忆。
        var size = IsUsable(perView) ? perView! : global;

        return new WindowBounds(global.Left, global.Top, size.Width, size.Height);
    }

    /// <summary>宽高必须为正 —— 0 尺寸的窗口是不可见/不可恢复的，视为"没存过"。</summary>
    private static bool IsUsable(WindowBounds? bounds) => bounds is { Width: > 0, Height: > 0 };
}
