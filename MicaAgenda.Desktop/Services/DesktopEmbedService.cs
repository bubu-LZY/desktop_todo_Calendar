using System;
using System.Runtime.InteropServices;

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

    private const int GwlExstyle = -20;
    private const int GwlStyle = -16;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const int WsExNoactivate = 0x08000000;
    private const int WsSysmenu = 0x00080000;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpFramechanged = 0x0020;
    private const uint SwpShowwindow = 0x0040;
    private static readonly IntPtr HwndBottom = new(1);

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
        SetWindowPos(
            handle,
            HwndBottom,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    /// <summary>
    /// 彻底移除标题栏上的系统按钮（最小化 / 最大化 / 关闭「×」）。
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
        if ((style & WsSysmenu) == 0)
        {
            return;
        }

        style &= ~WsSysmenu;
        SetWindowLong(handle, GwlStyle, style);

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

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "AttachThreadInput", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

}