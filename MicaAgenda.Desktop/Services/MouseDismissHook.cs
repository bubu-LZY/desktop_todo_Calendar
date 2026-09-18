using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;

namespace MicaAgenda.Desktop.Services;

/// <summary>
/// 低级鼠标钩子（WH_MOUSE_LL）：AI 对话覆盖层打开期间全局监听鼠标按下，
/// 让「点击程序外的桌面 / 其他窗口」也能关掉面板 —— 窗口内的点击由 AiBackdrop 处理，
/// 窗口外的点击只有钩子能拿到（覆盖层再大也出不了窗口）。
///
/// 低级钩子回调要求极快返回（否则系统会摘掉钩子），所以这里只解析坐标并
/// Post 回 UI 线程，命中判断与关闭动作都由宿主在 UI 线程完成；面板关闭即卸载。
/// </summary>
public sealed class MouseDismissHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;

    private IntPtr _hook;
    private HookProc? _proc;
    private Action<PixelPoint>? _onPress;

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public PointStruct Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>安装钩子。<paramref name="onPress"/> 在每次鼠标按下时收到屏幕像素坐标（UI 线程回调）。</summary>
    public void Install(Action<PixelPoint> onPress)
    {
        Uninstall();
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _onPress = onPress;
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            // 安装失败（权限等）：面板仍可用，只是失去「点外部关闭」能力，不算致命。
            _onPress = null;
            _proc = null;
        }
    }

    public void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _onPress = null;
        _proc = null;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _onPress is not null
            && (wParam == WmLButtonDown || wParam == WmRButtonDown))
        {
            var press = Marshal.PtrToStructure<MsllHookStruct>(lParam).Pt;
            var screen = new PixelPoint(press.X, press.Y);
            var handler = _onPress;
            Dispatcher.UIThread.Post(() => handler(screen));
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
