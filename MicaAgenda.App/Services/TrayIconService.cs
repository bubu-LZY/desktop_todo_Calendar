using System.Drawing;
using System.Windows.Forms;

namespace MicaAgenda.App.Services;

/// <summary>
/// 系统托盘图标：关闭窗口后常驻托盘，可重新显示、打开设置、退出。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showWindow;
    private readonly Action _openSettings;
    private readonly Action _exit;

    public TrayIconService(Action showWindow, Action openSettings, Action exit)
    {
        _showWindow = showWindow;
        _openSettings = openSettings;
        _exit = exit;

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MicaAgenda 桌面日历",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => _showWindow();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示日历", null, (_, _) => _showWindow());
        menu.Items.Add("设置", null, (_, _) => _openSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _exit());
        return menu;
    }

    public void ShowBalloonTip(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private static Icon LoadIcon()
    {
        try
        {
            // 从 exe 提取关联图标（单文件发布也能拿到）
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
            return icon ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
