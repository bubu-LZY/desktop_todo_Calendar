namespace MicaAgenda.App.Helpers;

/// <summary>
/// 「开机启动到底写哪一条」的决定。
///
/// 开机启动在这个程序里有两条互不相识的路径：
///   · HKCU\...\Run 注册表项（「开机自启动」，不需要管理员权限）；
///   · 任务计划程序里的登录任务（「高优先级开机启动」，需要管理员授权）。
/// 两条同时存在时，登录会被拉起两个进程 —— 用户看到的就是「一开机就冒出两个一样的窗口」。
/// 所以这里只允许留一条：计划任务在，注册表那条就不写、也不留。
///
/// 抽成一个纯函数是为了能直接单测：注册表和 schtasks 只在 Windows 上有，
/// 而这条判断本身与平台无关，跑偏了应该在跨平台单测里就红掉。
/// </summary>
public static class StartupPlan
{
    /// <summary>
    /// 要不要写注册表自启项。
    /// 计划任务已经登记时一律不写 —— 那不是"少写一条"，而是那条会让程序开机跑两个。
    /// </summary>
    public static bool ShouldWriteRunKey(bool autoStart, bool highPriorityTaskRegistered)
        => autoStart && !highPriorityTaskRegistered;
}
