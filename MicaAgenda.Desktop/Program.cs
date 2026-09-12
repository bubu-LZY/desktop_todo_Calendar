using Avalonia;

namespace MicaAgenda.Desktop;

internal static class Program
{
    // Avalonia 的 WinExe 入口：Main 必须在 STA 线程（Windows 上 UI 平台都要求 STA）
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // 拆出来是为了将来做"无头模式 / 集成测试"时能复用同一套 AppBuilder 配置
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}