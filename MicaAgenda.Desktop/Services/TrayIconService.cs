using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MicaAgenda.Desktop.Services;

/// <summary>
/// 系统托盘图标（Avalonia 版）：常驻托盘，可显示日历 / 打开设置 / 退出。
/// 桌面小部件右上角关闭按钮已被彻底移除，托盘是唯一的退出途径。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TrayIcon _tray;

    public TrayIconService(Action showWindow, Action openSettings, Action exit)
    {
        _tray = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "MicaAgenda 桌面日历",
            Menu = BuildMenu(showWindow, openSettings, exit)
        };
        _tray.Clicked += (_, _) => showWindow();

        // 托盘图标必须挂到 Application 上才会显示（跨平台：Windows / macOS / Linux）
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
    }

    private static NativeMenu BuildMenu(Action showWindow, Action openSettings, Action exit)
    {
        var menu = new NativeMenu();
        var showItem = new NativeMenuItem { Header = "显示日历" };
        showItem.Click += (_, _) => showWindow();
        var settingsItem = new NativeMenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => openSettings();
        var exitItem = new NativeMenuItem { Header = "退出" };
        exitItem.Click += (_, _) => exit();

        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private static WindowIcon LoadIcon()
        => new WindowIcon(AssetLoader.Open(new Uri("avares://MicaAgenda.Desktop/Assets/task-icon-32.png")));

    public void Dispose() => _tray.Dispose();
}