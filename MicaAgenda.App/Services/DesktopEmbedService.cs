using System.Runtime.InteropServices;
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
    /// 对最小化中的窗口等效于「还原」，用于把被「显示桌面」带走的窗口无声拉回来。
    /// </summary>
    private const int SwShowNoActivate = 4;

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
    /// 这里两种形态都认，任一命中就用 SW_SHOWNOACTIVATE 无声拉回。
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

        if (!IsIconic(handle) && IsWindowVisible(handle))
        {
            return false;
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
