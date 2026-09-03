using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MicaAgenda.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\MicaAgenda.DesktopTodoCalendar.SingleInstance";
    private const string ShowWindowEventName = @"Local\MicaAgenda.DesktopTodoCalendar.ShowWindow";

    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private bool _ownsMutex;
    private volatile bool _shuttingDown;

    public App()
    {
        EnsureWindirForWpf();
        SubscribeGlobalExceptionHandlers();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            // 已有实例在运行：通知它把窗口唤到前台，然后退出当前实例
            NotifyExistingInstance();
            Shutdown();
            return;
        }

        // 第一个实例：创建信号事件，后台监听其它实例的“显示窗口”请求
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _ = Task.Run(WaitForShowSignal);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;

        try { _showWindowEvent?.Set(); } catch { }
        try { _showWindowEvent?.Dispose(); } catch { }
        _showWindowEvent = null;

        if (_ownsMutex && _singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
        }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    private static void NotifyExistingInstance()
    {
        // 第一个实例创建事件与获取互斥锁之间有一个极小的窗口期，重试几次以覆盖它
        for (int i = 0; i < 5; i++)
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var showEvent))
            {
                using (showEvent)
                {
                    showEvent.Set();
                }
                return;
            }

            Thread.Sleep(100);
        }
    }

    private void WaitForShowSignal()
    {
        while (!_shuttingDown && _showWindowEvent is not null)
        {
            try
            {
                if (!_showWindowEvent.WaitOne(500))
                {
                    continue;
                }

                if (_shuttingDown)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _mainWindow?.BringToFront();
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "SingleInstance.BringToFront");
                    }
                });
            }
            catch
            {
                // 事件被释放或其它异常时退出监听
                return;
            }
        }
    }

    private void SubscribeGlobalExceptionHandlers()
    {
        // UI 线程上的 async void 异常（时钟计时器、Window_Loaded 等）不应直接终止程序
        this.Dispatcher.UnhandledException += (_, e) =>
        {
            LogError(e.Exception, "Dispatcher");
            e.Handled = true;
        };

        // 线程池上的 async void 异常（备份/提醒定时器）无法在此处拦截终止，仅记录日志
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

    /// <summary>记录异常到本地日志文件，便于排查；写入失败自身不抛异常。</summary>
    internal static void LogError(Exception? ex, string? context = null)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "MicaAgenda");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n";
            // 把 InnerException 也写出来，否则 XamlParseException 之类的包装异常就只剩一句泛话
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
        Environment.SetEnvironmentVariable("windir", string.IsNullOrWhiteSpace(systemRoot) ? @"C:\Windows" : systemRoot);
    }
}
