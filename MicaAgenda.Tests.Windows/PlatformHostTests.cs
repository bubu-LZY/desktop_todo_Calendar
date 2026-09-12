using System.Windows;
using Microsoft.Win32;
using MicaAgenda.App.Services;

namespace MicaAgenda.Tests.Windows;

/// <summary>
/// 只能跑在 Windows / WPF 宿主上的测试。
///
/// 这三个用例覆盖的实现（注册表自启、窗口材质像素换算、WPF 圆角裁剪）在
/// Avalonia 迁移中会被平台抽象层替换，届时随 WPF 工程一并重写或删除。
/// 单独成项目是为了不让 MicaAgenda.Tests 被绑在 net9.0-windows 上。
/// </summary>
public class PlatformHostTests
{
    [Fact]
    public void AutoStartService_WritesAndRemovesCurrentUserRunValue()
    {
        var id = Guid.NewGuid().ToString("N");
        var parentPath = $@"Software\MicaAgenda.Tests\{id}";
        var runPath = $@"{parentPath}\Run";
        var valueName = "MicaAgendaTest";
        var executablePath = @"C:\Apps\MicaAgenda\MicaAgenda.App.exe";

        try
        {
            AutoStartService.SetEnabled(true, runPath, valueName, executablePath);

            using (var key = Registry.CurrentUser.OpenSubKey(runPath, false))
            {
                Assert.Equal($"\"{executablePath}\"", key?.GetValue(valueName));
            }

            Assert.True(AutoStartService.IsEnabled(runPath, valueName));

            AutoStartService.SetEnabled(false, runPath, valueName, executablePath);

            using (var key = Registry.CurrentUser.OpenSubKey(runPath, false))
            {
                Assert.Null(key?.GetValue(valueName));
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKey(runPath, false);
            Registry.CurrentUser.DeleteSubKey(parentPath, false);
        }
    }

    [Theory]
    [InlineData(22, 1, 22)]
    [InlineData(22, 1.25, 28)]
    [InlineData(0, 1.5, 1)]
    public void WindowEffects_ToDevicePixels_RoundsUpAndKeepsPositiveRegion(int value, double scale, int expected)
    {
        Assert.Equal(expected, WindowEffects.ToDevicePixels(value, scale));
    }

    [Fact]
    public void MainWindow_CreateRoundedShellClip_MatchesShellSizeAndCornerRadius()
    {
        var clip = MicaAgenda.App.MainWindow.CreateRoundedShellClip(980, 680, 22);

        Assert.Equal(new Rect(0, 0, 980, 680), clip.Rect);
        Assert.Equal(22, clip.RadiusX);
        Assert.Equal(22, clip.RadiusY);
    }
}
