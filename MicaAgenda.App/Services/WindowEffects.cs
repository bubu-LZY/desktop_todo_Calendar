using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

public static class WindowEffects
{
    public static void Apply(Window window, CalendarBackgroundMode mode)
    {
        Disable(window);
    }

    /// <summary>
    /// 为原生嵌入模式启用系统背景材质与圆角。
    /// 需在窗口获得 HWND 之后调用。分层窗口（AllowsTransparency=True）不可用，
    /// 因此此方法仅用于非透明窗口。返回材质是否成功生效——
    /// 调用方应在失败时改用不透明背景，否则非分层窗口会渲染成黑色（桌面"不显示"）。
    /// </summary>
    public static bool EnableNativeBackdrop(Window window, CalendarBackgroundMode mode)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        EnableRoundedCorners(handle);

        // 优先 Mica（Win11 22H2+），失败回退到亚克力
        if (TryEnableMica(handle))
        {
            return true;
        }

        EnableAcrylic(handle, mode);
        // 亚克力通过 SetWindowCompositionAttribute 应用，无法直接取返回值；
        // 在顶层窗口上调用即视为成功，极少数失败情况由调用方兜底为不透明背景。
        return true;
    }

    private static void EnableRoundedCorners(IntPtr handle)
    {
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33，DWMWCP_ROUND = 2（仅 Win11）
        var preference = 2;
        try
        {
            DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch
        {
            // Win10 无此属性，忽略
        }
    }

    private static bool TryEnableMica(IntPtr handle)
    {
        // DWMWA_SYSTEMBACKDROP_TYPE = 38，DWMSBT_MAINWINDOW = 2（仅 Win11 22H2+）
        var backdrop = 2;
        try
        {
            var hr = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void EnableAcrylic(IntPtr handle, CalendarBackgroundMode mode)
    {
        var color = mode switch
        {
            CalendarBackgroundMode.FrostedDark or CalendarBackgroundMode.Graphite => unchecked((int)0xCC202124),
            CalendarBackgroundMode.AcrylicBlue => unchecked((int)0xCCDCEBFE),
            CalendarBackgroundMode.AcrylicMint => unchecked((int)0xCCD1FAE5),
            _ => unchecked((int)0xCCFFFFFF)
        };

        var accent = new AccentPolicy
        {
            AccentState = AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 0,
            GradientColor = color,
            AnimationId = 0
        };
        var accentSize = Marshal.SizeOf(accent);
        var accentPtr = Marshal.AllocHGlobal(accentSize);

        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.AccentPolicy,
                SizeOfData = accentSize,
                Data = accentPtr
            };
            SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    private static void Disable(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var accent = new AccentPolicy { AccentState = AccentState.Disabled };
        var accentSize = Marshal.SizeOf(accent);
        var accentPtr = Marshal.AllocHGlobal(accentSize);

        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.AccentPolicy,
                SizeOfData = accentSize,
                Data = accentPtr
            };
            SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal static int ToDevicePixels(double value, double scale)
    {
        return Math.Max(1, (int)Math.Ceiling(value * scale));
    }

    private enum WindowCompositionAttribute
    {
        AccentPolicy = 19
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableAcrylicBlurBehind = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
