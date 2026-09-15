using System;
using System.Threading;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.Desktop;

/// <summary>
/// 「同一时间只允许跑一个程序」的守门人。
///
/// 为什么必须有：这个 exe 可能被好几种方式同时拉起 ——
///   · 用户在桌面上把图标连点两下；
///   · 开机时注册表自启与计划任务各拉起一个（旧版本两条都写着）；
///   · 更新完自动重启时，上一份进程还没退干净。
/// 没有这道闸，这些情况都会变成"两个窗口、两份托盘图标、两套后台服务"，
/// 连 API / MCP 端口都会互相抢占。
///
/// 持锁失败的那一份不吭声退出，并顺手把已经在跑的那份叫到前面来，
/// 免得用户点了图标之后什么都没发生，以为程序坏了。
///
/// 锁用命名 Mutex：Windows 上默认就是「本会话」命名空间，正好对上"一个登录会话一个程序"。
/// 跨进程"叫醒"用命名事件，只有 Windows 支持 —— 其它平台没有这一步也不影响拦截重复启动。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "MicaAgenda_SingleInstance_Mutex";
    private const string ActivateEventName = "MicaAgenda_SingleInstance_Activate";

    /// <summary>当前进程持有的那份锁；拿不到锁的进程这里是 null。</summary>
    public static SingleInstanceGuard? Current { get; private set; }

    private readonly Mutex _mutex;
    private EventWaitHandle? _activate;
    private volatile bool _disposed;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// 试着成为"唯一的那一份"。返回 false 表示已经有一个实例在跑，本次启动应该直接退场
    /// （并且已经尝试把那个实例叫到前面）。
    /// </summary>
    public static bool TryAcquire(out SingleInstanceGuard? guard)
    {
        guard = null;

        Mutex mutex;
        try
        {
            // initiallyOwned: true —— 新锁建出来就归本进程；锁已存在时这个参数会被忽略。
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew && !TryTakeOver(mutex))
            {
                mutex.Dispose();
                SignalRunningInstance();
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 锁被另一个权限更高的实例持有（计划任务那份是 /RL HIGHEST）。
            // 打不开就等于有人在跑，目的已经达到。
            SignalRunningInstance();
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        Current = guard;
        return true;
    }

    /// <summary>
    /// 锁的名字已经被占用时，再看一眼它是不是真的还在别人手里：
    /// 上一份进程如果是没释放就退出的（崩溃 / 被结束），这里能把锁接过来，不会因为一次异常退出就再也开不了。
    /// </summary>
    private static bool TryTakeOver(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // 上一份进程没释放就退了：锁归我们，这不是错误。
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 开始听"又有人来启动程序了"的通知。<paramref name="activate"/> 在后台线程上被调用，
    /// 所以调用方自己要负责切回 UI 线程。
    /// </summary>
    public void ListenForActivation(Action activate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _activate = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception ex)
        {
            // 建不出事件只是"叫不醒"，不影响"只允许一个实例"这件事，没必要把启动搅黄。
            AppLog.Error(ex, "SingleInstanceGuard.Listen");
            return;
        }

        var listener = new Thread(() =>
        {
            try
            {
                // 用超时轮询而不是死等：退出时不必依赖别人记得来叫醒这个线程。
                while (!_disposed)
                {
                    if (!_activate.WaitOne(TimeSpan.FromMilliseconds(500)))
                    {
                        continue;
                    }

                    if (_disposed)
                    {
                        return;
                    }

                    activate();
                }
            }
            catch (Exception ex)
            {
                // 关程序时等待句柄可能正好被释放掉：这种情况不值得把进程带崩。
                if (!_disposed)
                {
                    AppLog.Error(ex, "SingleInstanceGuard.Listener");
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceActivateListener"
        };

        listener.Start();
    }

    /// <summary>把已经在跑的那一份叫到前面来。叫不动就算了，本次启动照样退出。</summary>
    private static void SignalRunningInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 前台许可先交给对方：本进程马上要退了，留着也用不上。
        Services.DesktopEmbedService.AllowForegroundHandoff();

        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SingleInstanceGuard.Signal");
        }
    }

    public void Dispose()
    {
        _disposed = true;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // 退出路径上不值得为释放失败再抛一次；进程一走锁自然就没了。
        }

        _mutex.Dispose();

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}
