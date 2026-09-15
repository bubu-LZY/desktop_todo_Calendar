using Avalonia;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.Desktop;

internal static class Program
{
    // Avalonia 的 WinExe 入口：Main 必须在 STA 线程（Windows 上 UI 平台都要求 STA）
    [STAThread]
    public static void Main(string[] args)
    {
        // 先把日志出口接上：下面几步都发生在 Avalonia 起来之前，出问题也得留下痕迹。
        AppLog.Sink = App.LogError;

        // 只能有一个实例：重复启动的那一份在这里就退场，连窗口都不建。
        // （开机时注册表自启与计划任务曾各拉起一个，用户看到的就是"一开机两个窗口"。）
        if (!SingleInstanceGuard.TryAcquire(out var guard))
        {
            AppLog.Error(null, "[STARTUP] 已有实例在运行，本次启动已退出");
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            guard?.Dispose();
        }
    }

    // 拆出来是为了将来做"无头模式 / 集成测试"时能复用同一套 AppBuilder 配置
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}