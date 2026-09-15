using MicaAgenda.App.Helpers;

namespace MicaAgenda.Tests;

/// <summary>
/// 开机启动只能留一条路：注册表自启与计划任务同时在，登录时会被拉起两个实例。
/// 这条判断与平台无关（注册表和 schtasks 的调用都在 Windows 侧），所以放在跨平台单测里。
/// </summary>
public sealed class StartupPlanTests
{
    [Fact]
    public void RunKeyIsWrittenWhenAutoStartIsOnAndNoTaskTakesOver()
    {
        Assert.True(StartupPlan.ShouldWriteRunKey(autoStart: true, highPriorityTaskRegistered: false));
    }

    [Fact]
    public void RegisteredTaskTakesOverTheRunKey_SoBootStartsOneInstanceOnly()
    {
        // 就是这个组合让老用户一开机看到两个程序：两条开机启动都写着。
        Assert.False(StartupPlan.ShouldWriteRunKey(autoStart: true, highPriorityTaskRegistered: true));
    }

    [Fact]
    public void NothingIsWrittenWhenAutoStartIsOff()
    {
        Assert.False(StartupPlan.ShouldWriteRunKey(autoStart: false, highPriorityTaskRegistered: false));
        Assert.False(StartupPlan.ShouldWriteRunKey(autoStart: false, highPriorityTaskRegistered: true));
    }
}
