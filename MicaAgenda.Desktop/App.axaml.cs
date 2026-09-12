using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.Desktop;

public partial class App : Application
{
    public App()
    {
        // Core（数据层/服务层）经 AppLog 记录异常，这里把 Sink 指向本宿主的写盘实现。
        // 与 WPF 宿主共用同一份实现（按 SpecialFolder.ApplicationData 落盘，跨平台）。
        AppLog.Sink = LogError;
        SubscribeGlobalExceptionHandlers();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void SubscribeGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogError(e.ExceptionObject as Exception, "AppDomain");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError(e.Exception, "TaskScheduler");
            e.SetObserved();
        };
        // 注意：Avalonia 目前没有和 WPF `Dispatcher.UnhandledException` 等价的全局 UI 线程异常钩子，
        // UI 线程异常会直接走到 AppDomain.UnhandledException。若要拦截并显示提示，需在迁移 UI 时
        // 加 `Dispatcher.UIThread.UnhandledException` 级别的处理（Avalonia 12 的 API，待确认）。
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
}