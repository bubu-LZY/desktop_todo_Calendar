using System;
using System.Runtime.InteropServices;

namespace MicaAgenda.Desktop.Services;

/// <summary>
/// Windows 10 上的窗口材质 / 外形辅助（Windows 专用，其余平台所有方法都应被跳过）。
///
/// 背景：本程序的窗口是「无边框 + 逐像素透明」（<c>WindowDecorations=None</c> +
/// <c>Background=Transparent</c> + <c>TransparencyLevelHint=Transparent</c>），在 Windows 上
/// 这会落成一块**分层窗口**（WS_EX_LAYERED）。分层窗口没有 DWM 合成缓冲：每一次重绘都要把
/// 整窗重新合成一遍，Win10 上表现为肉眼可见的闪烁（Win11 的 DWM 对这一路径做了改善，所以只在
/// Win10 上明显）。
///
/// 因此 Windows 10 改用 DWM 的**亚克力**材质（Windows 10 1803 / build 17134 起支持）：
/// 窗口不再是分层窗口，重绘交给 DWM 合成，闪烁随之消失；桌面壁纸透过亚克力被模糊，观感与
/// 「磨砂玻璃桌面挂件」一致。Windows 11 维持逐像素透明（圆角更锐利，也不必做下面的裁剪）。
///
/// 代价：亚克力是 DWM 画在**整块窗口矩形**上的，圆角外沿也会被填满 —— 四角会变成"磨砂直角"。
/// 所以这里额外提供 <see cref="ApplyRoundedRegion"/>：用 SetWindowRgn 把窗口外形裁成同样的
/// 圆角矩形，桌面从圆角外透出来。
/// </summary>
public static class WindowBackdropService
{
    /// <summary>亚克力材质的最低系统版本：Windows 10 1803。</summary>
    private const int AcrylicMinBuild = 17134;

    /// <summary>Windows 11 的起始 build（22000）。Win11 上保持逐像素透明。</summary>
    private const int Windows11Build = 22000;

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// 当前系统是否应当改用 DWM 亚克力材质 —— 即 Windows 10 1803 ~ Win10 末尾（build &lt; 22000）。
    ///
    /// 只认 Windows 10：Win11（build ≥ 22000）的 DWM 对分层窗口的重绘处理没有问题，保持原有的
    /// 逐像素透明即可（圆角锐利、透明度滑杆语义不变），不做无谓的行为变更。
    /// </summary>
    public static bool ShouldUseAcrylic
    {
        get
        {
            if (!IsWindows)
            {
                return false;
            }

            var build = Environment.OSVersion.Version.Build;
            return build is >= AcrylicMinBuild and < Windows11Build;
        }
    }

    /// <summary>
    /// 把窗口外形裁成圆角矩形（像素单位）。
    ///
    /// 只在亚克力模式下需要：此时圆角不能靠"窗口透明"表达（DWM 材质铺满整块矩形），必须由
    /// 窗口区域（RGN）来裁。失败时静默返回 —— 后果只是四角变直角，不影响窗口可用性。
    /// </summary>
    public static void ApplyRoundedRegion(IntPtr handle, double widthPx, double heightPx, double radiusPx)
    {
        if (!IsWindows || handle == IntPtr.Zero || widthPx < 1 || heightPx < 1)
        {
            return;
        }

        var width = (int)Math.Ceiling(widthPx);
        var height = (int)Math.Ceiling(heightPx);

        // 半径比外壳圆角小 1 像素、椭圆参数是直径（所以乘 2）、右/下边界是开区间（所以 +1）：
        // 让裁剪区域落在外壳圆角的**内侧**，免得圆角外沿的反锯齿边缘与亚克力之间露出一圈细缝。
        var radius = (int)Math.Round(Math.Max(0, radiusPx) - 1);
        var diameter = radius * 2;

        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(handle, region, true) == 0)
        {
            // 设置失败时区域所有权仍在我们手上，必须自己释放，否则句柄泄漏；
            // 成功时系统接管该区域，绝不能再删。
            DeleteObject(region);
        }
    }

    /// <summary>清掉窗口外形裁剪，回到普通矩形窗口（非亚克力模式用）。</summary>
    public static void ClearRegion(IntPtr handle)
    {
        if (!IsWindows || handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowRgn(handle, IntPtr.Zero, true);
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateRoundRectRgn", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int widthEllipse,
        int heightEllipse);

    [DllImport("user32.dll", EntryPoint = "SetWindowRgn", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);
}
