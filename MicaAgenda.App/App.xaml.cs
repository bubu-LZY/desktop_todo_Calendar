using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MicaAgenda.App;

public partial class App : Application
{
    // 与 Avalonia 宿主共用同一个（会话级）命名 Mutex：两个宿主默认写同一份数据文件，
    // 同时运行会互相覆盖，这里让它们互斥。会话级（不带 Global\ 前缀）无需管理员权限即可创建，
    // 且正好对应"一个登录会话一个实例"的语义。
    private static readonly Mutex _mutex = new(true, "MicaAgenda_SingleInstance_Mutex");

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public App()
    {
        // Core（数据层/服务层）经 AppLog 记录异常，这里把 Sink 指向本宿主的写盘实现
        Helpers.AppLog.Sink = LogError;
        EnsureWindirForWpf();
        SubscribeGlobalExceptionHandlers();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 检查是否已有实例
        if (!TryTakeSingleInstanceLock())
        {
            ActivateExistingWindow();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    private bool TryTakeSingleInstanceLock()
    {
        try
        {
            return _mutex.WaitOne(TimeSpan.Zero, true);
        }
        catch (AbandonedMutexException)
        {
            // 上一份进程没释放就退了（崩溃/被结束）：锁归我们，这不是错误。
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 打不开就当成"已有实例在跑"（与 Avalonia 侧同一策略），宁可本次退出也不要双开。
            return false;
        }
    }

    private void ActivateExistingWindow()
    {
        try
        {
            // 获取当前进程ID，避免把自己当成已有实例
            int currentPid = Process.GetCurrentProcess().Id;

            // 查找所有同名进程
            var processes = Process.GetProcessesByName("MicaAgenda.App");
            foreach (var proc in processes)
            {
                if (proc.Id == currentPid) continue;
                IntPtr hWnd = proc.MainWindowHandle;
                if (hWnd != IntPtr.Zero)
                {
                    // 如果窗口最小化，先恢复
                    ShowWindow(hWnd, SW_RESTORE);
                    // 激活窗口
                    SetForegroundWindow(hWnd);
                    break;
                }
            }
        }
        catch
        {
            // 忽略激活失败
        }
    }

    private void SubscribeGlobalExceptionHandlers()
    {
        this.Dispatcher.UnhandledException += (_, e) =>
        {
            LogError(e.Exception, "Dispatcher");
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogError(e.ExceptionObject as Exception, "AppDomain");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError(e.Exception, "TaskScheduler");
            e.SetObserved();
        };
    }

    internal static void LogError(Exception? ex, string? context = null)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "MicaAgenda");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n";
            var inner = ex?.InnerException;
            var depth = 0;
            while (inner is not null && depth < 5)
            {
                line += $"  -> Inner[{depth}] {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}\n";
                inner = inner.InnerException;
                depth++;
            }
            File.AppendAllText(Path.Combine(dir, "app-error.log"), line);
        }
        catch
        {
            // 忽略日志写入失败
        }
    }

    private static void EnsureWindirForWpf()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        Environment.SetEnvironmentVariable("windir", string.IsNullOrWhiteSpace(systemRoot) ? @"C:Windows" : systemRoot);
    }
}