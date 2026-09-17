using System;
using System.Runtime.InteropServices;
using System.Text;

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
    /// 对最小化中的窗口等效于「还原」，是把被「显示桌面」带走的窗口无声拉回来的正确指令；
    /// SW_RESTORE 会顺带激活窗口，用户刚点完显示桌面就被抢前台，体验很突兀。
    /// </summary>
    private const int SwShowNoActivate = 4;

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
    }

    /// <summary>
    /// 看门狗用：窗口一旦被「显示桌面」带走，立刻在<b>不激活、不抢焦点</b>的前提下还原。
    ///
    /// 触发场景：任务栏右键「显示桌面」、Win+D、点击屏幕右下角的显示桌面细条 ——
    /// Shell 会把所有普通顶层窗口（含本程序这种只做 Z 序置底、没挂 Progman/WorkerW 的窗口）
    /// 一并「收走」。本小部件又刻意隐藏了任务栏按钮，被收走后用户没有任何入口把它找回。
    ///
    /// <para><b>关键：Shell 收窗口有两种形态，只判 IsIconic 会漏掉一半。</b>
    /// 一是真正的「最小化」（IsIconic 为真）；二是把窗口 WS_VISIBLE 清零的直接隐藏
    /// （IsIconic 为假、IsWindowVisible 为假）—— 后者在部分 Windows 版本 / Shell 状态下
    /// 出现，表现就是「点了显示桌面，本程序窗口直接没了，看门狗也没动作」。这里两种形态
    /// 都认，任一命中就用 SW_SHOWNOACTIVATE 无声拉回。</para>
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

        if (!IsIconic(handle) && IsWindowVisible(handle))
        {
            return false;
        }

        // 被 Shell 藏起来的窗口 SW_SHOWNOACTIVATE 会连带「还原 + 显示」，无需先刷 WS_VISIBLE。
        ShowWindow(handle, SwShowNoActivate);
        return true;
    }

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