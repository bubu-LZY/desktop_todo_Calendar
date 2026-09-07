using System.Diagnostics;
using System.Text;

namespace MicaAgenda.App.Services;

/// <summary>
/// 通过 Windows 任务计划程序实现高优先级开机启动。
/// schtasks 的 /RL HIGHEST 可以以最高权限运行，配合 PowerShell Start-Process -Priority High 设置进程优先级。
/// </summary>
public static class HighPriorityStartupService
{
    private const string TaskName = "MicaAgenda_HighPriority";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            CreateTask();
        }
        else
        {
            RemoveTask();
        }
    }

    private static void CreateTask()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        // PowerShell 单引号字符串：把路径里的单引号转义为两个单引号，避免路径注入。
        var quotedExe = "'" + exePath.Replace("'", "''") + "'";
        var psCommand = $"Start-Process -FilePath {quotedExe} -Priority High";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(psCommand));
        var taskRun = $"powershell.exe -NoProfile -WindowStyle Hidden -EncodedCommand {encodedCommand}";
        var arguments = $"/Create /TN \"{TaskName}\" /TR \"{taskRun}\" /SC ONLOGON /RL HIGHEST /F";

        RunSchtasks(arguments);
    }

    private static void RemoveTask()
    {
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
    }

    private static void RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch
        {
            // 静默失败——任务计划程序需要管理员权限才能创建任务
        }
    }
}
