using System.Runtime.Versioning;
using System.ComponentModel;
using System.Diagnostics;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.App.Services;

/// <summary>
/// 「高优先级开机启动」。它包含两件独立的事，缺一不可：
///
/// 1) 进程优先级 —— 由程序自己在启动时把优先级提到 High。不依赖任何外部配置，必定生效。
/// 2) 开机触发 —— 在任务计划程序里登记一个登录时触发的任务。schtasks 建任务一律要管理员权限
///    （实测普通权限下即便建在自建子目录里也是 Access is denied），所以这一步会请求 UAC 授权。
///
/// 修掉的两个历史问题：
/// - 建任务的失败被静默吞掉：用户勾了选项却什么都没发生，也没有任何提示，看起来就是「没用」。
///   现在每一步都会把 schtasks 的输出与退出码带回来，并区分「需要授权 / 用户取消 / 真的失败」。
/// - 任务动作写的是 `powershell.exe ... Start-Process -Priority High`，而 Start-Process 根本没有
///   -Priority 参数（Windows PowerShell 5.1 和 PowerShell 7 都没有这个参数），
///   也就是说任务就算建成功也永远拉不起程序。现在任务直接执行 exe 本体，优先级由程序自己设置。
/// </summary>
[SupportedOSPlatform("windows")]
public static class HighPriorityStartupService
{
    internal const string TaskName = "MicaAgenda_HighPriority";

    public enum SetupStatus
    {
        Created,
        AlreadyPresent,
        Removed,
        NotPresent,
        NeedsElevation,
        Cancelled,
        Failed
    }

    public sealed record SetupResult(SetupStatus Status, string Message)
    {
        /// <summary>计划任务此刻是否处于「已登记」状态（以实际查询结果为准，不看本次操作声明）。</summary>
        public bool TaskRegistered => Status is SetupStatus.Created or SetupStatus.AlreadyPresent;
    }

    /// <summary>计划任务是否已登记（直接查询任务计划程序，失败一律视为未登记）。</summary>
    public static bool IsEnabled() => QueryTask();

    /// <summary>当前进程是否已经拿到管理员令牌。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 登记开机任务。allowElevation 为 true 时，权限不足会弹出 UAC 对话框。
    /// 注意：结果一律以「之后再查一次任务是否存在」为准，不轻信 schtasks 的退出码。
    /// </summary>
    public static SetupResult Enable(bool allowElevation)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new SetupResult(SetupStatus.Failed, "无法确定程序自身路径，未能登记高优先级启动项。");
        }

        // 任务已存在时不能一律当成「已登记」：还要看它指向的是不是当前这份 exe。
        // 典型场景 —— 旧版本用 WPF 宿主（MicaAgenda.App.exe）登记过任务，安装包升级时把旧宿主
        // 删掉了，任务于是成了死链：开机什么都不会发生，用户看到的就是「勾了高优先级启动却没作用」。
        var registered = GetRegisteredExePath();
        var stale = registered is not null && !IsSameExe(registered, exePath);
        if (!stale && QueryTask())
        {
            return new SetupResult(SetupStatus.AlreadyPresent, "高优先级启动项已登记。");
        }

        var arguments = BuildCreateArguments(exePath);
        var direct = RunSchtasks(arguments, elevated: false);
        if (NowRegisteredFor(exePath))
        {
            return new SetupResult(
                SetupStatus.Created,
                stale
                    ? "高优先级启动项原先指向旧版本的程序路径，已修正为当前程序。"
                    : "已登记高优先级启动项：下次登录会以最高权限自动启动。");
        }

        if (!allowElevation)
        {
            // 任务本来就在、只是没能修正（修正需要管理员权限）：如实回报「已登记」，
            // 免得设置界面把「已存在但指向旧宿主」误报成「需要管理员授权」。
            return stale
                ? new SetupResult(
                    SetupStatus.AlreadyPresent,
                    "高优先级启动项已登记，但仍指向旧版本的程序路径；保存设置时授权管理员即可修正。")
                : new SetupResult(SetupStatus.NeedsElevation, "登记高优先级启动项需要管理员权限。");
        }

        var elevated = RunSchtasks(arguments, elevated: true);
        if (elevated.Cancelled)
        {
            return new SetupResult(SetupStatus.Cancelled, "已取消管理员授权，高优先级启动项没有登记。");
        }

        if (NowRegisteredFor(exePath))
        {
            return new SetupResult(SetupStatus.Created, "已登记高优先级启动项：下次登录会以最高权限自动启动。");
        }

        return new SetupResult(SetupStatus.Failed, "登记高优先级启动项失败。" + DescribeFailure(direct, elevated));
    }

    /// <summary>任务当前是否「存在，并且（能解析出路径时）指向这份 exe」。</summary>
    private static bool NowRegisteredFor(string exePath)
    {
        if (!QueryTask())
        {
            return false;
        }

        var registered = GetRegisteredExePath();

        // 解析不出路径时不纠缠：任务确实存在就算成功，
        // 否则会把「查询输出格式变了」误判成「需要授权」，每次保存设置都白弹一次 UAC。
        return registered is null || IsSameExe(registered, exePath);
    }

    /// <summary>
    /// 计划任务当前指向的可执行文件（任务不存在 / 查询或解析失败返回 null）。
    /// 用 /XML 而不是 /V：动作路径在 XML 的 &lt;Command&gt; 里，比按列切本地化输出可靠得多。
    /// </summary>
    public static string? GetRegisteredExePath()
    {
        var run = RunSchtasks($"/Query /TN \"{TaskName}\" /XML", elevated: false);
        return run.Ok ? ParseRegisteredExePath(run.Output) : null;
    }

    /// <summary>从 schtasks /XML 的输出里取出 &lt;Command&gt;（即任务要跑的程序）。</summary>
    internal static string? ParseRegisteredExePath(string? queryOutput)
    {
        if (string.IsNullOrWhiteSpace(queryOutput))
        {
            return null;
        }

        // schtasks 可能按 UTF-16 输出，被 8 位解码后 ASCII 部分会夹着 NUL；先去掉再匹配。
        var text = queryOutput.Replace("\0", string.Empty);
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            "<Command>(.*?)</Command>",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var command = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim().Trim('"');
        return command.Length == 0 ? null : command;
    }

    /// <summary>两个路径是否指向同一个程序（忽略大小写与写法差异）。</summary>
    internal static bool IsSameExe(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(left.Trim().Trim('"')),
                System.IO.Path.GetFullPath(right.Trim().Trim('"')),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.Trim().Trim('"'), right.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>移除开机任务，并把进程优先级恢复为普通。</summary>
    public static SetupResult Disable(bool allowElevation = true)
    {
        // 关掉高优先级：先取消可能还在排队的「回落」定时器，再立刻把优先级拉回 Normal。
        CancelPriorityFallback();
        ApplyProcessPriority(false);

        if (!QueryTask())
        {
            return new SetupResult(SetupStatus.NotPresent, "高优先级启动已关闭。");
        }

        var arguments = BuildDeleteArguments();
        var direct = RunSchtasks(arguments, elevated: false);
        if (!QueryTask())
        {
            return new SetupResult(SetupStatus.Removed, "已移除高优先级启动项。");
        }

        if (!allowElevation)
        {
            return new SetupResult(SetupStatus.NeedsElevation, "移除高优先级启动项需要管理员权限。");
        }

        var elevated = RunSchtasks(arguments, elevated: true);
        if (elevated.Cancelled)
        {
            return new SetupResult(SetupStatus.Cancelled, "已取消管理员授权，高优先级启动项仍然保留。");
        }

        if (!QueryTask())
        {
            return new SetupResult(SetupStatus.Removed, "已移除高优先级启动项。");
        }

        return new SetupResult(SetupStatus.Failed, "移除高优先级启动项失败。" + DescribeFailure(direct, elevated));
    }

    /// <summary>
    /// 程序自己把进程优先级改成「高」。
    /// 之所以不依赖计划任务里的设置：开机时「注册表自启」和「计划任务」会各拉起一个实例，
    /// 抢单实例锁时谁先谁后不确定，只有自己设置才能保证最终活下来的实例一定是高优先级。
    /// </summary>
    public static bool ApplyProcessPriority(bool high)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var target = high ? ProcessPriorityClass.High : ProcessPriorityClass.Normal;
            if (current.PriorityClass != target)
            {
                current.PriorityClass = target;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "HighPriorityStartupService.ApplyProcessPriority");
            return false;
        }
    }

    /// <summary>启动后保持 High 优先级的时长：足够覆盖首屏渲染与初始化，之后回落到常驻档位。</summary>
    private static readonly TimeSpan HighPriorityWindow = TimeSpan.FromSeconds(15);

    private static Timer? _priorityFallbackTimer;

    /// <summary>
    /// 提升进程优先级，并在启动窗口结束后自动回落到常驻档位 <see cref="ProcessPriorityClass.AboveNormal"/>。
    ///
    /// High 只该在启动瞬间抢资源；桌面挂件长期挂 High 会跟前台全屏应用抢时间片，让整机手感变涩。
    /// AboveNormal 仍保留「高于普通应用」的调度偏好，但不会饿到前台 —— 既尊重「高优先级」的诉求，
    /// 又不牺牲整机响应。回退由一次性 Timer 触发，不占用 UI 线程。
    /// </summary>
    public static void ApplyProcessPriorityWithFallback(bool high)
    {
        CancelPriorityFallback();

        if (high)
        {
            ApplyProcessPriority(true);
            _priorityFallbackTimer = new Timer(
                _ =>
                {
                    _priorityFallbackTimer?.Dispose();
                    _priorityFallbackTimer = null;
                    ApplySustainedPriority();
                },
                null,
                HighPriorityWindow,
                Timeout.InfiniteTimeSpan);
        }
        else
        {
            ApplyProcessPriority(false);
        }
    }

    /// <summary>把进程优先级从 High 降到常驻的 AboveNormal（不是 High 就不动）。</summary>
    public static bool ApplySustainedPriority()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            if (current.PriorityClass == ProcessPriorityClass.High)
            {
                current.PriorityClass = ProcessPriorityClass.AboveNormal;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "HighPriorityStartupService.ApplySustainedPriority");
            return false;
        }
    }

    private static void CancelPriorityFallback()
    {
        _priorityFallbackTimer?.Dispose();
        _priorityFallbackTimer = null;
    }

    /// <summary>
    /// 拼 /Create 的命令行。任务直接跑 exe 本体，不再套 PowerShell ——
    /// 见类注释里说的 Start-Process -Priority 问题。
    ///
    /// /TR 的引号必须写成 \"路径\" 这种「反斜杠转义引号」形式。
    /// 写成两个双引号包住路径，会被 CreateProcess 拆成「空参数 + 裸路径」，
    /// schtasks 会直接报 Invalid argument/option（实测），任务永远建不起来；
    /// 安装路径通常带空格，所以这一层引号是必需的。
    /// </summary>
    internal static string BuildCreateArguments(string? exePath)
    {
        var cleaned = (exePath ?? string.Empty).Replace("\"", string.Empty);
        var quotedExe = "\\\"" + cleaned + "\\\"";
        return $"/Create /TN \"{TaskName}\" /TR \"{quotedExe}\" /SC ONLOGON /RL HIGHEST /F";
    }

    internal static string BuildDeleteArguments() => $"/Delete /TN \"{TaskName}\" /F";

    private static string DescribeFailure(SchtasksRun direct, SchtasksRun elevated)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(direct.Output)) parts.Add("直接创建：" + direct.Output);
        if (!string.IsNullOrWhiteSpace(elevated.Output)) parts.Add("授权创建：" + elevated.Output);
        if (parts.Count == 0) parts.Add("请确认系统允许创建计划任务。");
        return " " + string.Join("；", parts);
    }

    private static bool QueryTask()
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
            if (proc is null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct SchtasksRun(bool Ok, bool Cancelled, string Output);

    private static SchtasksRun RunSchtasks(string arguments, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = elevated,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (elevated)
            {
                psi.Verb = "runas";
            }
            else
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new SchtasksRun(false, false, "无法启动 schtasks.exe");
            }

            var output = string.Empty;
            if (!elevated)
            {
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                output = (stdout + " " + stderr).Trim();
            }

            // 授权路径要等用户在 UAC 对话框上操作，给足时间
            var timeout = elevated ? 180_000 : 10_000;
            if (!proc.WaitForExit(timeout))
            {
                try { proc.Kill(); } catch { /* 已经退出 */ }
                return new SchtasksRun(false, false, "schtasks 超时未返回");
            }

            return new SchtasksRun(proc.ExitCode == 0, false, output);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 对话框上点了「否」
            return new SchtasksRun(false, true, "用户取消了管理员授权");
        }
        catch (Exception ex)
        {
            return new SchtasksRun(false, false, ex.Message);
        }
    }
}
