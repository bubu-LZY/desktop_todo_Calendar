namespace MicaAgenda.App.Helpers;

/// <summary>
/// 平台无关的错误日志入口。
///
/// Core（数据层/服务层）要记录异常，但不能反过来依赖 UI 宿主的 App 类——那会把
/// Core 绑死在某个 UI 框架上。这里只暴露一个可注入的 Sink，由各 UI 宿主
/// （WPF / Avalonia）在启动时把它指向自己的写盘实现。
/// Sink 为 null 时静默丢弃，与"日志失败不影响主流程"的既有约定一致。
/// </summary>
public static class AppLog
{
    public static Action<Exception?, string?>? Sink { get; set; }

    public static void Error(Exception? ex, string? context = null)
    {
        try
        {
            Sink?.Invoke(ex, context);
        }
        catch
        {
            // 日志实现自身出错不能反过来打断业务
        }
    }
}
