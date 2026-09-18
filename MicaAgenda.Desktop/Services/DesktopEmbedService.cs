using System;
using System.Runtime.InteropServices;
using System.Text;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.Desktop.Services;

/// <summary>
/// 桌面嵌入（Windows 专用）：让窗口沉在其他普通窗口之下、隐藏任务栏按钮、禁止点击激活。
///
/// 与 WPF 版保持同一策略 —— 不挂 WorkerW / Progman 父窗口，而是「Z 序底部 + 周期性看门狗」：
/// 相比完全桌面模式，这种实现兼容 Win10 / Win11，不受 Explorer 桌面窗口层级变化影响。
///
/// macOS / Linux 没有「桌面嵌入」概念，调用方必须先判 <see cref="IsWindows"/>；
/// 非 Windows 平台本类所有方法都应被跳过（宿主持普通窗口即可）。
/// </summary>
public static class DesktopEmbedService
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>AllowSetForegroundWindow 的 ASFW_ANY：把前台许可发给任意进程。</summary>
    private const int AsfwAny = -1;

    private const int GwlExstyle = -20;
    private const int GwlStyle = -16;
    private const int GwHwndnext = 0x00000002;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const int WsExNoactivate = 0x08000000;
    private const int WsSysmenu = 0x00080000;
    private const int WsMinimizebox = 0x00020000;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpFramechanged = 0x0020;
    private static readonly IntPtr HwndBottom = new(1);

    /// <summary>
    /// ShowWindow 的 SW_SHOWNOACTIVATE：按最近一次的尺寸 / 位置显示窗口但<b>不激活、不抢焦点</b>。
    /// 注意它<b>不会</b>把窗口从最小化态恢复 —— 那件事要交给 <see cref="SwRestore"/>。
    /// </summary>
    private const int SwShowNoActivate = 4;

    /// <summary>
    /// ShowWindow 的 SW_RESTORE：真正把窗口从最小化态恢复成正常态。会顺带激活窗口，
    /// 所以在本类里只作为「先恢复、再取消激活」两步走的第一步使用，第二步固定是
    /// <see cref="SwShowNoActivate"/>。
    /// </summary>
    private const int SwRestore = 9;

    /// <summary>Explorer 桌面窗口的类名（Win10：Progman；Win11：WorkerW）。</summary>
    private const string ProgmanClass = "Progman";
    private const string WorkerWClass = "WorkerW";

    /// <summary>GetClassName 的缓冲区长度：桌面窗口类名很短，64 个字符足够。</summary>
    private const int ClassNameCapacity = 64;

    /// <summary>
    /// 隐藏任务栏按钮（设为 TOOLWINDOW，去掉 APPWINDOW）。
    /// 幂等：已经是目标样式就不写，方便周期性重放（样式会被宿主/系统改回去）。
    /// </summary>
    public static void HideFromTaskbar(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExstyle);
        var desired = (exStyle | WsExToolwindow) & ~WsExAppwindow;
        if (desired == exStyle)
        {
            return;
        }

        SetWindowLong(handle, GwlExstyle, desired);
    }

    /// <summary>
    /// 嵌入桌面模式下禁止窗口被点击激活：用户点日历任务时窗口不会先跳到普通窗口之上，
    /// 任务勾选等操作仍然可用。关闭嵌入模式时撤销该样式。
    /// </summary>
    public static void SetNoActivateStyle(IntPtr handle, bool noActivate)
    {
        if (!IsWindows || handle == IntPtr.Zero)
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

    /// <summary>将窗口推到 Z 序最底层（HWND_BOTTOM），使其位于所有普通窗口之下。</summary>
    public static void EmbedToDesktop(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        SetNoActivateStyle(handle, true);

        // 已经在最底层就不必再 SetWindowPos：看门狗每个 tick 都会调到这，反复压底会让窗口反复
        // 进出 DWM 的合成顺序，在 Win10 上表现为「连续闪烁」。
        //
        // 判据不能用「GW_HWNDNEXT 返回空」：桌面窗口（Progman / WorkerW）本身就在更低的 Z 序上，
        // 那个句柄正常情况下不会是空 —— 判据等于永远不成立，每 200ms 照样真压一次。这里改成
        // 顺着 Z 序往下找第一个可见的顶层窗口，只有它不是桌面窗口时，才说明我们头顶上还有普通
        // 窗口需要让位，才真的压底。
        if (IsAtDesktopLevel(handle))
        {
            return;
        }

        SetWindowPos(
            handle,
            HwndBottom,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate);
    }

    /// <summary>
    /// 窗口是否已经处在「桌面那一层」（即下面除了桌面窗口没有别的可见窗口）。
    ///
    /// 顺着 Z 序往下遍历，遇到的第一个可见顶层窗口如果是 Explorer 的桌面窗口，就说明我们已经在
    /// 最底层；一路走到头（下面没有窗口了）也算。任何一种情况都不需要再压底。
    /// </summary>
    private static bool IsAtDesktopLevel(IntPtr handle)
    {
        for (var window = GetWindow(handle, GwHwndnext); window != IntPtr.Zero; window = GetWindow(window, GwHwndnext))
        {
            if (!IsWindowVisible(window))
            {
                continue;
            }

            return IsDesktopWindow(window);
        }

        return true;
    }

    /// <summary>
    /// 是不是 Explorer 的桌面窗口：Win10 上是 Progman（外加若干 WorkerW），Win11 上是 WorkerW。
    /// 按类名判断即可 —— 这些窗口不一定属于本进程，也不适合按进程名去认。
    /// </summary>
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

    /// <summary>
    /// 彻底移除标题栏上的系统按钮（最小化 / 最大化 / 关闭「×」），并额外摘掉
    /// <c>WS_MINIMIZEBOX</c> —— 这是「免疫显示桌面」的第一道防线：Explorer 的
    /// Win+D / 显示桌面遍历窗口做 MinimizeAll 时会跳过不可最小化的窗口，
    /// 少了这个样式位，本窗口就不再被它当成「可以收起来的普通窗口」。
    ///
    /// 桌面小部件不该有可点的关闭按钮，退出走托盘 / 设置，而不是点 ×。
    /// 通过清掉 WS_SYSMENU 位实现：标题栏本身保留（标题文字还在），但三个系统按钮消失。
    ///
    /// 幂等：位已经清掉就直接返回。这样调用方可以周期性重放 —— 宿主/系统在激活、改尺寸、
    /// DPI 变化等时机重写窗口样式会把「×」放回来（用户看到的现象就是"叉号自己回来了"）。
    /// </summary>
    public static void RemoveCaptionButtons(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlStyle);
        var desired = style & ~WsSysmenu & ~WsMinimizebox;
        if (desired == style)
        {
            return;
        }

        SetWindowLong(handle, GwlStyle, desired);

        // SWP_FRAMECHANGED 触发一次非客户区重算，让按钮立即消失
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
    }

    /// <summary>窗口当前是不是系统前台窗口。</summary>
    public static bool IsForegroundWindow(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return false;
        }

        return GetForegroundWindow() == handle;
    }

    /// <summary>
    /// 请求把窗口带到前台（只在行内输入期间、已经放开激活之后才该调用）。
    ///
    /// SetForegroundWindow 有前台锁：只有"调用线程刚收到过用户输入"才允许抢占。嵌入桌面时
    /// 用户点的就是本窗口，通常一次就够；万一被拒就借一次前台窗口线程的输入队列再试
    /// （经典做法），避免输入框弹出来了却敲不进字。
    /// </summary>
    public static void RequestForeground(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        if (SetForegroundWindow(handle))
        {
            return;
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return;
        }

        if (!AttachThreadInput(foregroundThread, currentThread, true))
        {
            return;
        }

        try
        {
            SetForegroundWindow(handle);
        }
        finally
        {
            AttachThreadInput(foregroundThread, currentThread, false);
        }
    }

    /// <summary>
    /// 把"允许抢占前台"的许可发给别的进程（ASFW_ANY）。
    ///
    /// 用在「重复启动被拦下」的时候：刚被拉起的那一份握着前台许可，但它马上要退出；
    /// 先把这个许可交给已经在跑的那一份，它才有资格把窗口带到前台来（否则只会闪一下任务栏图标）。
    /// </summary>
    public static void AllowForegroundHandoff()
    {
        if (!IsWindows)
        {
            return;
        }

        try
        {
            AllowSetForegroundWindow(AsfwAny);
        }
        catch
        {
            // 许可给不出去无所谓：抢不到前台只是少一步贴心，不影响拦截重复启动。
        }
    }

    /// <summary>看门狗用的轻量组合：每个 tick 强制压底，防止窗口因激活浮到普通窗口之上。</summary>
    public static void EnsureEmbedded(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        SetNoActivateStyle(handle, true);
        EmbedToDesktop(handle);

        // 「显示桌面」把 WorkerW 提到顶端后，光压底反而等于把自己送到桌面下面去。
        // 压完必须再确认一次没被桌面层盖住。
        KeepAboveDesktopLayer(handle);
    }

    /// <summary>
    /// 看门狗用：窗口一旦被「显示桌面」带走，立刻在<b>不激活、不抢焦点</b>的前提下还原。
    ///
    /// 触发场景：任务栏右键「显示桌面」、Win+D、点击屏幕右下角的显示桌面细条 ——
    /// Shell 会把所有普通顶层窗口（含本程序这种只做 Z 序置底、没挂 Progman/WorkerW 的窗口）
    /// 一并「收走」。本小部件又刻意隐藏了任务栏按钮，被收走后用户没有任何入口把它找回。
    ///
    /// <para><b>Shell 收窗口有两种形态，只判 IsIconic 会漏掉一半。</b>
    /// 一是真正的「最小化」（IsIconic 为真）；二是把窗口 WS_VISIBLE 清零的直接隐藏
    /// （IsIconic 为假、IsWindowVisible 为假）—— 后者在部分 Windows 版本 / Shell 状态下
    /// 出现，表现就是「点了显示桌面，本程序窗口直接没了，看门狗也没动作」。这里两种形态
    /// 都认。</para>
    ///
    /// <para><b>关键：还原必须分两步，SW_SHOWNOACTIVATE 单独用是修不好的。</b>
    /// <c>SW_SHOWNOACTIVATE(4)</c> 只负责「按最近尺寸显示」，它<b>不会把窗口从最小化态恢复</b> ——
    /// 对一个 IsIconic 为真的窗口调它，窗口仍然是最小化的（只是被"显示"成一条任务栏条目）。
    /// 这就是上一版"看门狗明明在跑、窗口却没回来"的真正原因。正确序列是先
    /// <c>SW_RESTORE(9)</c> 真正脱离最小化态，再 <c>SW_SHOWNOACTIVATE</c> 确保它不抢前台焦点
    /// （SW_RESTORE 会顺带激活窗口，用户刚点完显示桌面就被抢前台，体验很突兀）。</para>
    ///
    /// <para><b>托盘「隐藏」不会误伤</b>：它走的是 Avalonia 的 Hide()，但那一步是我们自己发起的，
    /// 调用方会先置 <see cref="SuppressAutoRestore"/>（见 <c>MainWindow</c> 的托盘隐藏路径），
    /// 因此不会被这里的自动还原立刻拉回来。</para>
    ///
    /// 返回 true 表示本次 tick 确实发生了还原，调用方应紧接着重新压一次 Z 序底。
    /// </summary>
    public static bool RestoreIfMinimized(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero || SuppressAutoRestore)
        {
            return false;
        }

        LastRestoreWasMinimized = false;
        LastRestoreWasHidden = false;
        LastRestoreSucceeded = false;

        var minimized = IsIconic(handle);
        if (!minimized && IsWindowVisible(handle))
        {
            return false;
        }

        // 记下命中的是哪种形态：用户报"显示桌面后窗口不见了"时，
        // 日志里这条就是区分"被最小化"还是"被直接隐藏"的唯一依据。
        LastRestoreWasMinimized = minimized;
        LastRestoreWasHidden = !minimized && !IsWindowVisible(handle);

        if (minimized)
        {
            // 必须先真正脱离最小化态；SW_SHOWNOACTIVATE 不干这件事。
            ShowWindow(handle, SwRestore);
        }

        // 无论哪条路径，最后都用 SW_SHOWNOACTIVATE 收尾：把窗口显示出来但不抢前台。
        ShowWindow(handle, SwShowNoActivate);

        // 复核还原是否真的落地。Shell 在「显示桌面」期间会持续把窗口往最小化压，
        // 我们的 ShowWindow 有可能刚好落在它两次施压的缝里、立刻又被收走 ——
        // 那时窗口仍然是不可见的，调用方据此可以在同一个 tick 内再拉一次，
        // 而不是傻等 200ms 后的下一轮 tick（用户感知就是"窗口消失了一会儿"）。
        LastRestoreSucceeded = !IsIconic(handle) && IsWindowVisible(handle);
        return true;
    }

    /// <summary>
    /// 上一次 <see cref="RestoreIfMinimized"/> 调用的还原是否真的生效
    /// （调用后复查 <c>!IsIconic &amp;&amp; IsWindowVisible</c>）。
    ///
    /// 为 false 说明还原指令发出去了但窗口又被压回不可见 —— 典型场景是「显示桌面」
    /// 正在连续施压。调用方应对同一窗口立即重试，而不是等下一个看门狗 tick。
    /// </summary>
    public static bool LastRestoreSucceeded { get; private set; }

    /// <summary>
    /// 防止窗口被「显示桌面」浮上来的桌面层<b>盖住</b>。
    ///
    /// <para><b>这是"显示桌面后窗口看起来消失了"的真因。</b>实测 Z 序：
    /// 正常时是「普通窗口 → 本窗口 → WorkerW(桌面)」，本窗口在桌面之上所以看得见；
    /// 一点「显示桌面」，Windows 会把 <c>WorkerW</c> 整体提到 Z 序顶端
    /// （仅次于任务栏），于是变成「WorkerW → 普通窗口 → 本窗口」——
    /// 本窗口 <c>IsWindowVisible</c> 仍然为 true、也没有被最小化，
    /// <b>它只是被桌面挡住了</b>。所以只查 vis / ico 永远查不出问题，必须看 Z 序。</para>
    ///
    /// 修法：发现上方存在可见的桌面层窗口时，把本窗口插到最高的那个桌面层<b>之上</b>一位。
    /// 这样它在桌面上重新可见；而「显示桌面」时其它普通窗口都已被最小化，
    /// 这样做并不会破坏"沉在其他窗口之下"的观感。
    /// </summary>
    public static void KeepAboveDesktopLayer(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        // 缓存"桌面层窗口"句柄：显示桌面把 WorkerW 提上来后它会一直待在 Z 序顶部，
        // 频繁全量遍历 Z 序（每次 50~150 个窗口 × GetClassName）是纯浪费。
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

        // 从 Z 序顶端往下走：先遇到自己 → 说明桌面层都在我们下面，正常，不用动。
        for (var window = GetTopWindow(IntPtr.Zero);
             window != IntPtr.Zero;
             window = GetWindow(window, GwHwndnext))
        {
            if (window == handle)
            {
                return;
            }

            if (!IsWindowVisible(window) || !IsDesktopWindow(window))
            {
                continue;
            }

            _cachedDesktopLayer = window;

            // 桌面层压在我们上面：把自己挪到它上一位（更靠近用户）。
            // 传 GW_HWNDPREV 作为 hWndInsertAfter = 插到那个窗口之前。
            SetWindowPos(
                handle,
                GetWindow(window, GwHwndprev),
                0,
                0,
                0,
                0,
                SwpNomove | SwpNosize | SwpNoactivate);
            return;
        }
    }

    private static IntPtr _cachedDesktopLayer;

    /// <summary>target 是否排在 self 的 Z 序之上（即 target 会盖住 self）。</summary>
    private static bool IsAboveUs(IntPtr self, IntPtr target)
    {
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

    /// <summary>GetWindow 的 GW_HWNDPREV：Z 序中位于其上的那个窗口。</summary>
    private const int GwHwndprev = 0x00000003;

    /// <summary>
    /// 窗口当前是不是处于「被收走」的两种形态之一（最小化 或 WS_VISIBLE 被清零）。
    /// </summary>
    public static bool IsHiddenOrMinimized(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return false;
        }

        return IsIconic(handle) || !IsWindowVisible(handle);
    }

    // ===== 兜底：不依赖 Avalonia，直接从 Win32 层找回主窗口 =====

    /// <summary>
    /// <b>不依赖 Avalonia</b> 地找回本进程的主窗口句柄。
    ///
    /// <para><b>为什么需要它。</b>窗口被「显示桌面」收走后，Avalonia 的
    /// <c>TryGetPlatformHandle()</c> 可能返回 null 或旧句柄（原生窗口被重建 / 销毁），
    /// 于是看门狗在死句柄上空转 —— 这正是「窗口彻底不回来、只能右键托盘打开」的成因。
    /// 这里直接从 Win32 层按进程 ID 枚举，拿到<b>此刻真实存在</b>的窗口。</para>
    ///
    /// 识别条件与诊断脚本一致：类名以 <c>Avalonia-</c> 开头，但要排除
    /// <c>AvaloniaSimpleWindow-</c>（渲染/输入辅助窗口）和 <c>AvaloniaMessageWindow</c>。
    /// </summary>
    public static IntPtr FindOwnMainWindow()
    {
        if (!IsWindows)
        {
            return IntPtr.Zero;
        }

        var pid = GetCurrentProcessId();
        var found = IntPtr.Zero;

        try
        {
            EnumWindows(
                (handle, _) =>
                {
                    var owner = GetWindowThreadProcessIdOut(handle, out var processId);
                    if (processId != pid)
                    {
                        return true;      // 继续枚举
                    }

                    var buffer = new StringBuilder(ClassNameCapacity);
                    if (GetClassName(handle, buffer, buffer.Capacity) <= 0)
                    {
                        return true;
                    }

                    var name = buffer.ToString();
                    if (!name.StartsWith("Avalonia-", StringComparison.Ordinal)
                        || name.StartsWith("AvaloniaSimpleWindow", StringComparison.Ordinal)
                        || name.StartsWith("AvaloniaMessageWindow", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    found = handle;
                    return false;         // 找到了，停止枚举
                },
                IntPtr.Zero);
        }
        catch
        {
            return IntPtr.Zero;
        }

        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")]
    private static extern uint GetCurrentProcessId();

    // 单独起名：与上面已有的 GetWindowThreadProcessId(IntPtr, IntPtr) 重载区分，
    // 两个 P/Invoke 指向同一个 Win32 函数但签名不同。
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    private static extern uint GetWindowThreadProcessIdOut(IntPtr hWnd, out uint lpdwProcessId);

    // ===== 拦在源头：子类化窗口过程，直接吃掉「最小化」消息 =====

    /// <summary>
    /// 被守卫的窗口句柄（0 表示未安装）。
    /// </summary>
    private static IntPtr _guardedHandle;

    /// <summary>
    /// 原窗口过程指针。必须保存 —— 子类化只是"插一层"，最终仍要把消息转回去，
    /// 否则窗口会失去全部原生行为（拖动、缩放、绘制全废）。
    /// </summary>
    private static IntPtr _originalWndProc;

    /// <summary>
    /// 由 GC 保活的委托。native 侧只持有函数指针，如果托管委托被回收，
    /// 窗口收到消息时就会跳进已释放的内存 —— 这是子类化最经典的崩溃原因。
    /// </summary>
    private static WndProcDelegate? _wndProcKeepAlive;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>GWL_WNDPROC：窗口过程指针在 32/64 位下都是这个索引。</summary>
    private const int GwlWndproc = -4;

    private const uint WmSyscommand = 0x0112;
    private const uint WmSize = 0x0005;
    private const int ScMinimize = 0xF020;
    private const int ScRestore = 0xF120;
    private const int SizeMinimized = 1;

    /// <summary>
    /// 给窗口装一个「最小化免疫」钩子：把 <c>SC_MINIMIZE</c> 和 <c>SIZE_MINIMIZED</c> 直接吃掉，
    /// 让窗口<b>根本不会进入</b>最小化态。
    ///
    /// <para><b>为什么光摘 WS_MINIMIZEBOX 不够。</b>那个样式位只影响两件事：标题栏有没有最小化
    /// 按钮、以及 Explorer 做 MinimizeAll 时要不要跳过本窗口。它对<b>直接发过来的</b>
    /// <c>WM_SYSCOMMAND/SC_MINIMIZE</c> 毫无约束 —— 实测连发几次，窗口照样被逐个压成
    /// 最小化，看门狗 200ms 后才拉回来，用户看到的就是「闪一下没了」。</para>
    ///
    /// <para><b>为什么不能靠"还原得再快一点"。</b>轮询始终存在一个最坏 200ms 的窗口期；
    /// 而「显示桌面」施压是连续的，压回去的速度可以一直快过我们拉回来。
    /// 唯一稳的办法是在消息抵达窗口过程时就不让它生效 —— 这时窗口从未最小化，
    /// 也就不存在"恢复不及时"的问题。</para>
    ///
    /// 幂等：已经守卫过同一句柄就直接返回。句柄变化（Avalonia 重建原生窗口）时会重新装。
    /// </summary>
    public static void GuardAgainstMinimize(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        if (_guardedHandle == handle && _originalWndProc != IntPtr.Zero)
        {
            return;
        }

        _wndProcKeepAlive = WndProc;
        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive);
        var previous = SubclassWindow(handle, newProc);
        if (previous == IntPtr.Zero)
        {
            // 装不上就放弃（看门狗那两层防线仍然在）。不抛异常：这只是加固，不是必需路径。
            _wndProcKeepAlive = null;
            return;
        }

        _originalWndProc = previous;
        _guardedHandle = handle;
    }

    private static IntPtr SubclassWindow(IntPtr handle, IntPtr newProc)
    {
        // 64 位用 SetWindowLongPtrW；32 位进程里该导出不存在，回落到 SetWindowLongW。
        if (IntPtr.Size == 8)
        {
            return SetWindowLongPtr(handle, GwlWndproc, newProc);
        }

        return SetWindowLong32(handle, GwlWndproc, newProc.ToInt32());
    }

    /// <summary>
    /// 替换后的窗口过程：吞掉最小化相关消息，其余原样转发。
    ///
    /// 注意这里运行在窗口的消息线程上，<b>绝不能抛异常</b> —— 异常穿过 native 边界会直接
    /// 终止进程。所以整段用 try/catch 兜住，出错时退化为"什么都不拦"。
    /// </summary>
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (ShouldSwallowMessage(msg, wParam, lParam))
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
    /// 部分路径直接送 <c>WM_SIZE / SIZE_MINIMIZED</c>。
    /// 恢复类消息（SC_RESTORE）<b>不拦</b> —— 那是我们自己还原时要走的路。
    ///
    /// 我们自己主动走 <c>ShowWindow(SW_MINIMIZE)</c> 时也必须放行，否则托盘/设置里的
    /// 「最小化」按钮会失效；那种场景由 <see cref="SuppressAutoRestore"/> 标记，这里一并放行。
    /// </summary>
    private static bool ShouldSwallowMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (SuppressAutoRestore)
        {
            return false;
        }

        if (msg == WmSyscommand)
        {
            // 低 4 位是系统命令码的高位区，取低 16 位再比。
            var command = wParam.ToInt64() & 0xFFF0;
            return command == ScMinimize;
        }

        if (msg == WmSize)
        {
            // wParam 低 16 位 = 尺寸类型；SIZE_MINIMIZED(1) 才会把窗口变成最小化态。
            var sizeType = wParam.ToInt64() & 0xFFFF;
            return sizeType == SizeMinimized;
        }

        return false;
    }

    /// <summary>上一次 <see cref="RestoreIfMinimized"/> 命中的形态：是否"被最小化"。</summary>
    public static bool LastRestoreWasMinimized { get; private set; }

    /// <summary>上一次 <see cref="RestoreIfMinimized"/> 命中的形态：是否"被直接隐藏"（WS_VISIBLE 被清零）。</summary>
    public static bool LastRestoreWasHidden { get; private set; }

    // ===== 事件驱动：前台窗口变化时立刻纠一次 Z 序（替代高频轮询）=====

    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;

    private static IntPtr _foregroundHook;
    private static Action? _foregroundChanged;
    private static IntPtr _lastForeground;

    /// <summary>由 GC 保活的回调委托（native 侧只持函数指针，和 WndProc 子类化同一道理）。</summary>
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
    /// 这是把看门狗从 50ms 高频轮询降到 2s 低频巡检的前提：轮询最坏要等一个 tick 才把被
    /// 「显示桌面」盖住的窗口拉回来，事件钩子则在前台切换的瞬间就触发，体感无延迟。
    /// 回调投递到安装线程（UI 线程）的消息循环里，和看门狗 tick 同线程，安全。
    /// </summary>
    public static void InstallForegroundWatch(Action onForegroundChanged)
    {
        if (!IsWindows)
        {
            return;
        }

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

    /// <summary>
    /// 把一次真实发生的自动还原写进错误日志，作为「显示桌面带走窗口」的现场证据。
    ///
    /// 正常情况下这条日志不该频繁出现（用户不点显示桌面就不会有）。如果用户报告
    /// "窗口消失" 而日志里一条都没有，说明窗口是被本类判定之外的途径收走的，
    /// 需要重新审视 <c>IsIconic</c> / <c>IsWindowVisible</c> 这对判据。
    /// 每 2 秒最多记一条，避免看门狗高频 tick 把日志刷爆。
    /// </summary>
    public static void LogRestoreEvent(IntPtr handle, string stage = "after-restore")
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRestoreLogAt).TotalSeconds < 2)
        {
            return;
        }

        _lastRestoreLogAt = now;

        var style = GetWindowLong(handle, GwlStyle);
        var exStyle = GetWindowLong(handle, GwlExstyle);
        var form = LastRestoreWasMinimized ? "minimized" : (LastRestoreWasHidden ? "hidden" : "unknown");

        AppLog.Error(
            null,
            $"[ShowDesktopWatchdog] auto-restored window (stage={stage} form={form} " +
            $"WS_VISIBLE={(style & WsVisible) != 0} WS_MINIMIZE={(style & WsMinimize) != 0} " +
            $"WS_MINIMIZEBOX={(style & WsMinimizebox) != 0} " +
            $"WS_EX_NOACTIVATE={(exStyle & WsExNoactivate) != 0})");
    }

    private static DateTime _lastRestoreLogAt = DateTime.MinValue;

    private const int WsVisible = 0x10000000;
    private const int WsMinimize = 0x20000000;

    /// <summary>
    /// 自动还原的临时闸门：托盘「隐藏」/ 用户主动最小化的那几步会把它置 true，
    /// 让看门狗在这一瞬间不要跟用户的操作对着干（否则窗口会立刻被拉回来，表现为"隐藏不掉"）。
    /// 由 <c>MainWindow</c> 在操作前后成对开关。
    /// </summary>
    public static bool SuppressAutoRestore { get; set; }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindow", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "IsIconic", SetLastError = true)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "ShowWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "AllowSetForegroundWindow")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", EntryPoint = "AttachThreadInput", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

}