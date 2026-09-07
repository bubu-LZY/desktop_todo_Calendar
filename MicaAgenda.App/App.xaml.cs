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
    private static readonly Mutex _mutex = new(true, "Global\\MicaAgenda_SingleInstance_Mutex");

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public App()
    {
        EnsureWindirForWpf();
        SubscribeGlobalExceptionHandlers();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 检查是否已有实例
        if (!_mutex.WaitOne(TimeSpan.Zero, true))
        {
            ActivateExistingWindow();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
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