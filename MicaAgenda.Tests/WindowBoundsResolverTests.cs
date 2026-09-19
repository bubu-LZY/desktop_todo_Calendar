using MicaAgenda.App.Models;

namespace MicaAgenda.Tests;

/// <summary>
/// 启动恢复窗口几何的取值规则。
///
/// 这一组测试守的是一个真实发生过的 bug：写入侧把分视图条目的 Left/Top 抹成 0，
/// 读取侧却把整条记录当完整边界用 —— 于是每次重启窗口都落在屏幕左上角
/// （用户原话：「重启之后依旧没有记住之前的位置，默认打开就在左上角」）。
/// </summary>
public sealed class WindowBoundsResolverTests
{
    /// <summary>宿主提供的兜底边界（当前位置 + 该视图默认尺寸）。</summary>
    private static readonly WindowBounds HostFallback = new(120, 90, 900, 620);

    [Fact]
    public void ForStartup_LegacyPerViewZeroCoordinates_DoNotDragWindowToTopLeft()
    {
        // 用户机器上真实存在的存档：通用记忆有正确位置，分视图条目是早期版本写入的「Left/Top 全 0」。
        var settings = new CalendarSettings
        {
            ViewMode = CalendarViewMode.Week,
            WindowBounds = new WindowBounds(1070, 21, 492.6666666666667, 500),
            WeekWindowBounds = new WindowBounds(0, 0, 492.6666666666667, 500)
        };

        var result = WindowBoundsResolver.ForStartup(settings, CalendarViewMode.Week, HostFallback);

        // 位置必须来自通用记忆 —— 这正是「重启后跑到左上角」的修复点。
        Assert.Equal(1070, result.Left);
        Assert.Equal(21, result.Top);
        // 尺寸仍用该视图自己的记忆。
        Assert.Equal(492.6666666666667, result.Width);
        Assert.Equal(500, result.Height);
    }

    [Fact]
    public void ForStartup_PositionComesFromGlobalEvenWhenPerViewHasItsOwnPosition()
    {
        // 位置只认通用记忆：分视图条目里即便存了别的坐标也不参与恢复，
        // 否则切视图时窗口会在屏幕上乱跳（切视图本来就保持位置、只换尺寸）。
        var settings = new CalendarSettings
        {
            WindowBounds = new WindowBounds(300, 200, 900, 620),
            MonthWindowBounds = new WindowBounds(10, 10, 910, 520)
        };

        var result = WindowBoundsResolver.ForStartup(settings, CalendarViewMode.Month, HostFallback);

        Assert.Equal(300, result.Left);
        Assert.Equal(200, result.Top);
        Assert.Equal(910, result.Width);   // 尺寸取分视图记忆
        Assert.Equal(520, result.Height);
    }

    [Fact]
    public void ForStartup_UsesGlobalSizeWhenPerViewMemoryIsMissing()
    {
        var settings = new CalendarSettings
        {
            WindowBounds = new WindowBounds(400, 150, 880, 600),
            WeekWindowBounds = null
        };

        var result = WindowBoundsResolver.ForStartup(settings, CalendarViewMode.Week, HostFallback);

        Assert.Equal(new WindowBounds(400, 150, 880, 600), result);
    }

    [Fact]
    public void ForStartup_IgnoresPerViewEntriesOfOtherViews()
    {
        // 启动在月视图：周视图 / 年视图的记忆不该影响结果。
        var settings = new CalendarSettings
        {
            WindowBounds = new WindowBounds(250, 130, 900, 620),
            WeekWindowBounds = new WindowBounds(0, 0, 480, 500),
            YearWindowBounds = new WindowBounds(0, 0, 700, 420)
        };

        var result = WindowBoundsResolver.ForStartup(settings, CalendarViewMode.Month, HostFallback);

        Assert.Equal(new WindowBounds(250, 130, 900, 620), result);
    }

    [Fact]
    public void ForStartup_TasksViewFallsBackToGlobalMemory()
    {
        // 任务视图没有自己的记忆槽位，应直接用通用记忆（位置 + 尺寸）。
        var settings = new CalendarSettings
        {
            WindowBounds = new WindowBounds(520, 240, 760, 560),
            WeekWindowBounds = new WindowBounds(1, 2, 480, 500)
        };

        var result = WindowBoundsResolver.ForStartup(settings, CalendarViewMode.Tasks, HostFallback);

        Assert.Equal(new WindowBounds(520, 240, 760, 560), result);
    }

    [Fact]
    public void ForStartup_ZeroSizedPerViewMemoryIsTreatedAsUnset()
    {
        // 宽高非正 = 不可恢复的窗口，视为"没存过"，退回通用记忆的尺寸。
        var settings = new CalendarSettings
        {
            WindowBounds = new WindowBounds(600, 300, 900, 620),
            MonthWindowBounds = new WindowBounds(0, 0, 0, 0)
        };

        var result = WindowBoundsResolver.ForStartup(settings, CalendarViewMode.Month, HostFallback);

        Assert.Equal(new WindowBounds(600, 300, 900, 620), result);
    }

    [Fact]
    public void ForStartup_UnusableGlobalMemory_FallsBackToHostBounds()
    {
        // 通用记忆被写成空记录（JSON 里 null 或全 0）时，整体退回宿主给的兜底边界。
        var settings = new CalendarSettings
        {
            WindowBounds = new WindowBounds(0, 0, 0, 0),
            MonthWindowBounds = new WindowBounds(0, 0, 910, 520)
        };

        var result = WindowBoundsResolver.ForStartup(settings, CalendarViewMode.Month, HostFallback);

        Assert.Equal(HostFallback.Left, result.Left);
        Assert.Equal(HostFallback.Top, result.Top);
        // 尺寸依然优先用分视图记忆。
        Assert.Equal(910, result.Width);
        Assert.Equal(520, result.Height);
    }

    [Fact]
    public void ForStartup_FreshInstallReturnsDefaultGlobalBounds()
    {
        // 全新安装：CalendarSettings.WindowBounds 自带默认值 (120,90,980,680)，
        // 不该出现「左上角」这种意外落点。
        var settings = new CalendarSettings();

        var result = WindowBoundsResolver.ForStartup(settings, CalendarViewMode.Month, HostFallback);

        Assert.Equal(new WindowBounds(120, 90, 980, 680), result);
        Assert.NotEqual(0, result.Left);
        Assert.NotEqual(0, result.Top);
    }
}
