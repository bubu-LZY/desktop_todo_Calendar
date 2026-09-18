using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MicaAgenda.App.Services;

/// <summary>
/// 桌面嵌入：让窗口保持在其他普通窗口之下，并隐藏任务栏按钮。
/// 该模式不改变窗口父级，只使用 Z 序底部 + 周期看门狗；
/// 相比挂到 WorkerW 的“完全桌面”模式，这个实现兼容 Win10 / Win11，
/// 不会受 Explorer 桌面窗口层级变化影响。
/// </summary>
public static class DesktopEmbedService
{
    private const int GwlExstyle = -20;
    private const int GwlStyle = -16;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const int WsExNoactivate = 0x08000000;
    private const int WsSysmenu = 0x00080000;
    private const int WsMinimizebox = 0x00020000;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpFramechanged = 0x0020;
    private const int HwndBottom = 1;

    /// <summary>
    /// ShowWindow 的 SW_SHOWNOACTIVATE：按最近尺寸 / 位置显示窗口但不激活、不抢焦点。
    /// 注意它<b>不会</b>把窗口从最小化态恢复，那件事交给 <see cref="SwRestore"/>。
    /// </summary>
    private const int SwShowNoActivate = 4;

    /// <summary>
    /// ShowWindow 的 SW_RESTORE：真正把窗口从最小化态恢复成正常态。会顺带激活窗口，
    /// 所以只作为「先恢复、再取消激活」两步走的第一步使用。
    /// </summary>
    private const int SwRestore = 9;

    /// <summary>隐藏任务栏图标（设为 TOOLWINDOW，去 APPWINDOW）。</summary>
    public static void HideFromTaskbar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExstyle);
        exStyle |= WsExToolwindow;
        exStyle &= ~WsExAppwindow;
        SetWindowLong(handle, GwlExstyle, exStyle);
    }

    /// <summary>
    /// 嵌入桌面模式默认禁止窗口被点击激活：这样用户点击日历任务时，
    /// 窗口不会先跳到普通窗口之上，任务勾选操作仍然可用。
    /// 关闭嵌入模式时撤销该样式。
    /// </summary>
    public static void SetNoActivateStyle(Window window, bool noActivate)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExstyle);
        var desired = noActivate ? (exStyle | WsExNoactivate) : (exStyle & ~WsExNoactivate);
        if (desired == exStyle)
        {
            // 已经是目标样式就不重复写：SetWindowLong 每次都会触发一次窗口风格变更通知，
            // 而看门狗每个 tick 都会调到这里，反复写同一个值纯属浪费（还可能引起重绘）。
            return;
        }

        SetWindowLong(handle, GwlExstyle, desired);
    }

    /// <summary>
    /// 将窗口推到 Z 序最底层（HWND_BOTTOM），使其位于所有普通窗口之下。
    /// 周期性调用以抵消其他窗口对 Z 序的影响。
    /// </summary>
    public static void EmbedToDesktop(Window window)
    {
        SetNoActivateStyle(window, true);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            (IntPtr)HwndBottom,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    /// <summary>
    /// 看门狗使用的轻量检查：每个 tick 强制压底，保证窗口不会因 WPF 激活而浮到普通窗口之上。
    /// </summary>
    public static void EnsureEmbedded(Window window)
    {
        SetNoActivateStyle(window, true);
        EmbedToDesktop(window);
    }

    /// <summary>从托盘唤出后，确保窗口仍沉在其他窗口之下、不抢焦点。</summary>
    public static void SendToBottom(Window window)
    {
        EmbedToDesktop(window);
    }

    /// <summary>
    /// 摘掉窗口的 <c>WS_MINIMIZEBOX</c> 样式位 —— 「免疫显示桌面」的第一道防线：
    /// Explorer 的 Win+D / 显示桌面遍历窗口做 MinimizeAll 时会跳过不可最小化的窗口，
    /// 少了这个位，本窗口就不再被它当成「可以收起来的普通窗口」，从源头减少被带走的概率
    /// （<see cref="RestoreIfMinimized"/> 是兜底的第二道防线）。
    ///
    /// 幂等：位已经清掉就直接返回，方便看门狗周期性重放（样式会被宿主/系统改回去）。
    /// </summary>
    public static void StripMinimizeBox(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlStyle);
        var desired = style & ~WsMinimizebox & ~WsSysmenu;
        if (desired == style)
        {
            return;
        }

        SetWindowLong(handle, GwlStyle, desired);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
    }

    /// <summary>
    /// 看门狗用：窗口一旦被「显示桌面」带走，立刻在不激活、不抢焦点的前提下还原。
    ///
    /// 任务栏右键「显示桌面」、Win+D、右下角显示桌面细条会把所有普通顶层窗口「收走」，
    /// 而本窗口刻意没有任务栏按钮，被收走后用户没有入口找回。
    ///
    /// <b>Shell 收窗口有两种形态，只判 IsIconic 会漏掉一半：</b>一是真正的最小化
    /// （IsIconic 为真）；二是把窗口 WS_VISIBLE 清零的直接隐藏（IsIconic 为假、
    /// IsWindowVisible 为假）—— 后者表现就是「点了显示桌面，程序窗口直接没了，看门狗也没动作」。
    /// 这里两种形态都认。
    ///
    /// <b>还原必须分两步：</b><c>SW_SHOWNOACTIVATE(4)</c> 只负责「按最近尺寸显示」，
    /// <b>不会把窗口从最小化态恢复</b>——对最小化窗口单独调它，窗口仍然是最小化的。
    /// 正确序列是先 <c>SW_RESTORE(9)</c> 真正脱离最小化态，再 <c>SW_SHOWNOACTIVATE</c>
    /// 确保不抢前台焦点。
    ///
    /// 托盘「隐藏」是我们自己发起的 <c>Hide()</c>，那一步会先打开
    /// <see cref="SuppressAutoRestore"/> 闸门，因此不会被这里的自动还原立刻拉回来。
    /// 返回 true 表示本次确实发生了还原（调用方可紧接着重新压底）。
    /// </summary>
    public static bool RestoreIfMinimized(Window window)
    {
        if (SuppressAutoRestore)
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var minimized = IsIconic(handle);
        if (!minimized && IsWindowVisible(handle))
        {
            return false;
        }

        if (minimized)
        {
            ShowWindow(handle, SwRestore);
        }

        ShowWindow(handle, SwShowNoActivate);
        return true;
    }

    /// <summary>
    /// 自动还原的临时闸门：托盘「隐藏」等我们主动收起窗口的那几步会把它置 true，
    /// 让看门狗在这一瞬间不要跟用户的操作对着干（否则窗口会立刻被拉回来，表现为"隐藏不掉"）。
    /// 由 <c>MainWindow</c> 在操作前后成对开关。
    /// </summary>
    public static bool SuppressAutoRestore { get; set; }

    // ===== 事件驱动：前台窗口变化时立刻纠一次 Z 序（替代高频轮询）=====

    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;

    private static IntPtr _foregroundHook;
    private static Action? _foregroundChanged;
    private static IntPtr _lastForeground;

    private static readonly WinEventProc ForegroundHookProc = OnForegroundChanged;

    private delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", EntryPoint = "SetWinEventHook", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc pfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    /// <summary>
    /// 装一个「前台窗口变化」事件钩子：一旦发生（含「显示桌面」把桌面提为前台），
    /// 立刻回调 <paramref name="onForegroundChanged"/> 纠一次 Z 序。
    ///
    /// 这是把看门狗从 200ms 轮询降到 2s 低频巡检的前提：事件钩子在前台切换瞬间就触发，体感无延迟。
    /// 回调投递到安装线程（UI 线程）的消息循环里，和看门狗 tick 同线程，安全。
    /// </summary>
    public static void InstallForegroundWatch(Action onForegroundChanged)
    {
        _foregroundChanged = onForegroundChanged;
        if (_foregroundHook != IntPtr.Zero)
        {
            return;
        }

        try
        {
            _foregroundHook = SetWinEventHook(
                EventSystemForeground, EventSystemForeground,
                IntPtr.Zero, ForegroundHookProc, 0, 0, WineventOutofcontext);
        }
        catch
        {
            // 装不上就退回纯轮询（看门狗仍在），不影响主流程。
            _foregroundHook = IntPtr.Zero;
        }
    }

    private static void OnForegroundChanged(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint thread, uint time)
    {
        try
        {
            if (hwnd == _lastForeground)
            {
                return;
            }

            _lastForeground = hwnd;
            _foregroundChanged?.Invoke();
        }
        catch
        {
            // 回调跑在 UI 线程消息循环里，绝不能抛异常穿过 native 边界。
        }
    }

    // ===== 拦在源头：子类化窗口过程，直接吃掉「最小化」消息 =====

    private static IntPtr _guardedHandle;
    private static IntPtr _originalWndProc;
    private static WndProcDelegate? _wndProcKeepAlive;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GwlWndproc = -4;
    private const uint WmSyscommand = 0x0112;
    private const uint WmSize = 0x0005;
    private const int ScMinimize = 0xF020;
    private const int SizeMinimized = 1;

    /// <summary>
    /// 给窗口装一个「最小化免疫」钩子：把 <c>SC_MINIMIZE</c> 和 <c>SIZE_MINIMIZED</c> 直接吃掉，
    /// 让窗口<b>根本不会进入</b>最小化态（与 Avalonia 宿主同一策略）。
    ///
    /// <para><b>为什么光摘 WS_MINIMIZEBOX 不够。</b>那个样式位只影响两件事：标题栏有没有最小化
    /// 按钮、以及 Explorer 做 MinimizeAll 时要不要跳过本窗口。它对<b>直接发过来的</b>
    /// <c>WM_SYSCOMMAND/SC_MINIMIZE</c> 毫无约束 —— 实测连发几次，窗口照样被逐个压成最小化，
    /// 看门狗 200ms 后才拉回来，用户看到的就是「闪一下没了」。</para>
    ///
    /// <para><b>为什么不能靠"还原得再快一点"。</b>轮询始终存在最坏 200ms 的窗口期，
    /// 而施压是连续的，压回去的速度可以一直快过我们拉回来。唯一稳的办法是在消息抵达窗口过程时
    /// 就不让它生效 —— 这时窗口从未最小化，也就不存在"恢复不及时"。</para>
    ///
    /// 幂等：已经守卫过同一句柄就直接返回。句柄变化时会重新装。
    /// </summary>
    public static void GuardAgainstMinimize(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (_guardedHandle == handle && _originalWndProc != IntPtr.Zero)
        {
            return;
        }

        _wndProcKeepAlive = WndProc;
        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive);
        var previous = IntPtr.Size == 8
            ? SetWindowLongPtr(handle, GwlWndproc, newProc)
            : new IntPtr(SetWindowLong32(handle, GwlWndproc, newProc.ToInt32()));

        if (previous == IntPtr.Zero)
        {
            // 装不上就放弃（看门狗那两层防线仍在）。这只是加固，不是必需路径。
            _wndProcKeepAlive = null;
            return;
        }

        _originalWndProc = previous;
        _guardedHandle = handle;
    }

    /// <summary>
    /// 替换后的窗口过程：吞掉最小化相关消息，其余原样转发。
    ///
    /// 这里运行在窗口的消息线程上，<b>绝不能抛异常</b> —— 异常穿过 native 边界会直接终止进程。
    /// 所以整段兜住，出错时退化为"什么都不拦"。
    /// </summary>
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (ShouldSwallowMessage(msg, wParam))
            {
                // 返回 0 = "处理完了"，窗口不会真的最小化。
                return IntPtr.Zero;
            }
        }
        catch
        {
            // 判定过程本身出错时不要拦，让消息正常走完。
        }

        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 这条消息是不是"想让窗口最小化"。命中就吞掉。
    ///
    /// 覆盖两条真实路径：Shell 批量最小化发 <c>SC_MINIMIZE</c>（WM_SYSCOMMAND 0xF020），
    /// 部分路径直接送 <c>WM_SIZE / SIZE_MINIMIZED</c>。恢复类消息不拦 —— 我们自己还原时要走。
    /// 我们主动调用（托盘「最小化」按钮）时由 <see cref="SuppressAutoRestore"/> 放行。
    /// </summary>
    private static bool ShouldSwallowMessage(uint msg, IntPtr wParam)
    {
        if (SuppressAutoRestore)
        {
            return false;
        }

        if (msg == WmSyscommand)
        {
            return (wParam.ToInt64() & 0xFFF0) == ScMinimize;
        }

        if (msg == WmSize)
        {
            return (wParam.ToInt64() & 0xFFFF) == SizeMinimized;
        }

        return false;
    }

    /// <summary>
    /// 防止窗口被「显示桌面」浮上来的桌面层<b>盖住</b>（与 Avalonia 宿主同一策略）。
    ///
    /// <b>这是"显示桌面后窗口看起来消失了"的真因。</b>实测 Z 序：正常时本窗口在
    /// WorkerW(桌面) 之上所以看得见；一点「显示桌面」，Windows 把 WorkerW 整体提到
    /// Z 序顶端，于是本窗口虽然 <c>IsWindowVisible</c> 仍为 true、也没被最小化，
    /// 却<b>被桌面挡住了</b>。只查 vis / ico 永远查不出来，必须看 Z 序。
    /// </summary>
    public static void KeepAboveDesktopLayer(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // 缓存"桌面层窗口"的句柄：显示桌面把 WorkerW 提上来后它会一直待在 Z 序顶部，
        // 频繁全量遍历 Z 序（每次 50~150 个窗口 × GetClassName）是纯浪费。
        // 缓存的句柄失效（桌面窗口被重建）时回退到全量遍历重新定位一次。
        if (_cachedDesktopLayer != IntPtr.Zero)
        {
            if (!IsWindow(_cachedDesktopLayer))
            {
                _cachedDesktopLayer = IntPtr.Zero;
            }
            else if (IsAboveUs(handle, _cachedDesktopLayer))
            {
                SetWindowPos(handle, GetWindow(_cachedDesktopLayer, GwHwndprev), 0, 0, 0, 0,
                    SwpNomove | SwpNosize | SwpNoactivate);
                return;
            }
        }

        for (var w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = GetWindow(w, GwHwndnext))
        {
            if (w == handle)
            {
                return;   // 先遇到自己 → 桌面层都在我们下面，正常
            }

            if (!IsWindowVisible(w) || !IsDesktopWindow(w))
            {
                continue;
            }

            _cachedDesktopLayer = w;
            SetWindowPos(handle, GetWindow(w, GwHwndprev), 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNoactivate);
            return;
        }
    }

    private static IntPtr _cachedDesktopLayer;

    /// <summary>target 是否排在 self 的 Z 序之上（即 target 会盖住 self）。</summary>
    private static bool IsAboveUs(IntPtr self, IntPtr target)
    {
        // 从 self 往上（GW_HWNDPREV 方向）走，遇到 target 说明它在 self 之上。
        for (var w = GetWindow(self, GwHwndprev); w != IntPtr.Zero; w = GetWindow(w, GwHwndprev))
        {
            if (w == target)
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll", EntryPoint = "IsWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetTopWindow", SetLastError = true)]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindow", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    private const int GwHwndprev = 0x00000003;
    private const int GwHwndnext = 0x00000002;

    private const string ProgmanClass = "Progman";
    private const string WorkerWClass = "WorkerW";
    private const int ClassNameCapacity = 64;

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static bool IsDesktopWindow(IntPtr handle)
    {
        var buffer = new StringBuilder(ClassNameCapacity);
        if (GetClassName(handle, buffer, buffer.Capacity) <= 0)
        {
            return false;
        }

        var name = buffer.ToString();
        return name.Equals(ProgmanClass, StringComparison.OrdinalIgnoreCase)
            || name.Equals(WorkerWClass, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>窗口当前是不是处于「被收走」的两种形态之一（最小化 或 WS_VISIBLE 被清零）。</summary>
    public static bool IsHiddenOrMinimized(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return IsIconic(handle) || !IsWindowVisible(handle);
    }

    /// <summary>锁定窗口：禁止拖动/缩放，固定当前位置。</summary>
    public static void LockWindow(Window window)
    {
        window.ResizeMode = ResizeMode.NoResize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "IsIconic", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "ShowWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
