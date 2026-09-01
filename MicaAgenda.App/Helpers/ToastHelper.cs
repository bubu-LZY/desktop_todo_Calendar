using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MicaAgenda.App.Helpers;

/// <summary>
/// 轻量级Toast通知，2-3秒自动消失，无声音
/// </summary>
public static class ToastHelper
{
    private static Window? _toastWindow;
    private static DispatcherTimer? _currentTimer;

    /// <summary>
    /// 显示Toast通知
    /// </summary>
    public static void Show(string message, Window? owner = null)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 关闭之前的toast和定时器
                Cleanup();

                var toast = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    Width = 300,
                    Height = 50,
                    ResizeMode = ResizeMode.NoResize,
                    Owner = owner
                };

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(230, 50, 50, 50)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 10, 16, 10),
                    Margin = new Thickness(10)
                };

                var textBlock = new TextBlock
                {
                    Text = message,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };

                border.Child = textBlock;
                toast.Content = border;

                // 定位到屏幕右下角
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                toast.Left = screenWidth - toast.Width - 20;
                toast.Top = screenHeight - toast.Height - 60;

                // 淡入动画
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                toast.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                toast.Show();
                _toastWindow = toast;

                // 2.5秒后淡出并关闭
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2.5)
                };
                _currentTimer = timer;
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _currentTimer = null;
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                    fadeOut.Completed += (s2, e2) =>
                    {
                        try
                        {
                            toast.Close();
                        }
                        catch { }
                        _toastWindow = null;
                    };
                    toast.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                };
                timer.Start();
            });
        }
        catch
        {
            // 忽略Toast显示错误
        }
    }

    /// <summary>
    /// 清理Toast窗口和定时器
    /// </summary>
    public static void Cleanup()
    {
        try
        {
            _currentTimer?.Stop();
            _currentTimer = null;

            if (_toastWindow != null)
            {
                _toastWindow.Close();
                _toastWindow = null;
            }
        }
        catch
        {
            _toastWindow = null;
        }
    }
}
