using MicaAgenda.App.Services;

namespace MicaAgenda.Tests;

/// <summary>
/// 「高优先级开机启动」登记计划任务时拼出来的命令行。
/// 这两条曾经出过问题：
/// - 任务动作以前套了一层 `powershell.exe ... Start-Process -Priority High`，
///   而 Start-Process 并没有 -Priority 参数，任务建起来也拉不动程序；
/// - 现在任务直接执行 exe 本体，所以路径必须用嵌套引号包住，否则带空格的安装路径会被截断。
/// </summary>
public sealed class HighPriorityStartupServiceTests
{
    [Fact]
    public void BuildCreateArguments_ContainsRequiredSchtasksSwitches()
    {
        var args = HighPriorityStartupService.BuildCreateArguments(@"C:\Apps\MicaAgenda.App.exe");

        Assert.Contains("/Create", args);
        Assert.Contains($"/TN \"{HighPriorityStartupService.TaskName}\"", args);
        Assert.Contains("/SC ONLOGON", args);
        Assert.Contains("/RL HIGHEST", args);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void BuildCreateArguments_RunsExeDirectly_NotThroughPowerShell()
    {
        var args = HighPriorityStartupService.BuildCreateArguments(@"C:\Apps\MicaAgenda.App.exe");

        Assert.DoesNotContain("powershell", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", args, StringComparison.OrdinalIgnoreCase);
        // 关键：-Priority 不是 Start-Process 的参数，任务动作里绝不能再出现它
        Assert.DoesNotContain("-Priority", args, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Program Files\desktop_todo_Calendar\MicaAgenda.App.exe")]
    [InlineData(@"C:\Apps\MicaAgenda.App.exe")]
    public void BuildCreateArguments_QuotesExePathForSchtasks(string exePath)
    {
        var args = HighPriorityStartupService.BuildCreateArguments(exePath);

        // /TR 用「反斜杠转义引号」包住路径；写成两个双引号会被 CreateProcess 拆坏
        Assert.Contains($"/TR \"\\\"{exePath}\\\"\"", args);
    }

    [Fact]
    public void BuildCreateArguments_StripsEmbeddedQuotesFromPath()
    {
        var args = HighPriorityStartupService.BuildCreateArguments("C:\\Apps\\we\"ird\\app.exe");

        Assert.DoesNotContain("we\"ird", args);
        Assert.Contains("weird", args);
    }

    [Fact]
    public void BuildCreateArguments_ToleratesMissingPath()
    {
        var args = HighPriorityStartupService.BuildCreateArguments(null);

        Assert.Contains("/Create", args);
        Assert.Contains("/SC ONLOGON", args);
    }

    [Fact]
    public void BuildDeleteArguments_TargetsTheSameTask()
    {
        var args = HighPriorityStartupService.BuildDeleteArguments();

        Assert.Contains("/Delete", args);
        Assert.Contains($"/TN \"{HighPriorityStartupService.TaskName}\"", args);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void TaskRegistered_OnlyReflectsCreatedOrAlreadyPresent()
    {
        Assert.True(new HighPriorityStartupService.SetupResult(
            HighPriorityStartupService.SetupStatus.Created, "x").TaskRegistered);
        Assert.True(new HighPriorityStartupService.SetupResult(
            HighPriorityStartupService.SetupStatus.AlreadyPresent, "x").TaskRegistered);
        Assert.False(new HighPriorityStartupService.SetupResult(
            HighPriorityStartupService.SetupStatus.NeedsElevation, "x").TaskRegistered);
        Assert.False(new HighPriorityStartupService.SetupResult(
            HighPriorityStartupService.SetupStatus.Cancelled, "x").TaskRegistered);
        Assert.False(new HighPriorityStartupService.SetupResult(
            HighPriorityStartupService.SetupStatus.Failed, "x").TaskRegistered);
    }
}
