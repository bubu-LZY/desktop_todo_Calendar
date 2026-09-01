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
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const int WsExNoactivate = 0x08000000;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const int HwndBottom = 1;

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
}
