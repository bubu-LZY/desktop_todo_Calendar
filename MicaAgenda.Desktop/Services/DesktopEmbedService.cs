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

    /// <summary>隐藏任务栏按钮（设为 TOOLWINDOW，去掉 APPWINDOW）。</summary>
    public static void HideFromTaskbar(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExstyle);
        exStyle |= WsExToolwindow;
        exStyle &= ~WsExAppwindow;
        SetWindowLong(handle, GwlExstyle, exStyle);
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
        if (noActivate)
        {
            exStyle |= WsExNoactivate;
        }
        else
        {
            exStyle &= ~WsExNoactivate;
        }

        SetWindowLong(handle, GwlExstyle, exStyle);
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
    /// </summary>
    public static void RemoveCaptionButtons(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlStyle);
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
}