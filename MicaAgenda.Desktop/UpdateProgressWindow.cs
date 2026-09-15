using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.Desktop;

/// <summary>
/// 「正在下载更新包」的小浮窗：实时进度条 + 失败重试。
///
/// 为什么单独做一个窗而不是塞进设置面板：更新包有 50MB 上下，下载期间用户还要继续用日历，
/// 所以做成**非模态**浮窗。它固定屏幕居中 + 置顶 —— 用户上报过「更新时的按钮被设置面板挡住、
/// 点不动」，浮动提示一旦落到底下的窗口后面就直接失效，这里从窗口层级上杜绝这件事。
///
/// 只管画界面与收事件，下载流程（含重试循环）留在 <see cref="MainWindow"/>，
/// 与「检查更新 → 询问 → 下载 → 重启」的既有链路待在一起。
/// </summary>
internal sealed class UpdateProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _titleText;
    private readonly TextBlock _detailText;
    private readonly TextBlock _errorText;
    private readonly Button _cancelButton;
    private readonly Button _retryButton;
    private readonly Button _closeButton;
    private readonly CancellationTokenSource _cts = new();
    private readonly long _expectedBytes;
    private readonly IProgress<double> _progress;

    private TaskCompletionSource<bool>? _retryChoice;

    public UpdateProgressWindow(string versionText, long expectedBytes)
    {
        _expectedBytes = expectedBytes;
        _progress = new Progress<double>(Report);

        Title = "正在下载更新";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        // 屏幕居中 + 置顶：无论设置面板 / 主窗体在什么层级，这个窗都不会被压到后面去。
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.Parse("#F7F8FA"));
        FontSize = 13;

        _titleText = new TextBlock
        {
            Text = $"正在下载 {versionText}…",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            IsIndeterminate = true
        };

        _detailText = new TextBlock
        {
            Text = "正在连接…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280"))
        };

        _errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#B42318")),
            IsVisible = false
        };

        _cancelButton = MakeButton("取消", primary: false);
        _retryButton = MakeButton("重试", primary: true);
        _closeButton = MakeButton("关闭", primary: false);
        _retryButton.IsVisible = false;
        _closeButton.IsVisible = false;

        _cancelButton.Click += (_, _) => Dismiss(cancelDownload: true);
        _retryButton.Click += (_, _) => CompleteRetry(again: true);
        _closeButton.Click += (_, _) => CompleteRetry(again: false);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                _titleText,
                _bar,
                _detailText,
                _errorText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { _cancelButton, _retryButton, _closeButton }
                }
            }
        };

        // 点右上角 × 关窗 = 放弃：下载中当取消、失败后当不再重试
        Closed += (_, _) => Finish();
    }

    /// <summary>下载用的取消令牌（点「取消」或关窗时触发）。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>下载进度（0~1），由 UpdateService 报告。</summary>
    public IProgress<double> Progress => _progress;

    /// <summary>换成「重新下载」的文案（点过重试之后）。</summary>
    public void ShowRetrying()
    {
        _errorText.IsVisible = false;
        _retryButton.IsVisible = false;
        _closeButton.IsVisible = false;
        _cancelButton.IsVisible = true;
        _detailText.Text = "正在连接…";
        _bar.IsIndeterminate = true;
    }

    /// <summary>下载失败：亮出错误原因，并在原地给出「重试」——不用重新走一遍检查更新。</summary>
    public void ShowFailed(string message)
    {
        _bar.IsIndeterminate = false;
        _errorText.Text = "下载失败：" + message;
        _errorText.IsVisible = true;
        _detailText.Text = string.Empty;
        _cancelButton.IsVisible = false;
        _retryButton.IsVisible = true;
        _closeButton.IsVisible = true;
        _retryChoice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>等用户在失败态里选「重试」（true）或「关闭」（false）。</summary>
    public Task<bool> WaitForRetryAsync()
        => _retryChoice?.Task ?? Task.FromResult(false);

    /// <summary>下载完成：进度拉满，随即自动关窗。</summary>
    public void ShowCompleted()
    {
        _bar.IsIndeterminate = false;
        _bar.Value = 100;
        if (_expectedBytes > 0)
        {
            _detailText.Text = $"{ByteText.FormatSize(_expectedBytes)} / {ByteText.FormatSize(_expectedBytes)}（100%）";
        }
        else
        {
            _detailText.Text = "100%";
        }

        _cancelButton.IsVisible = false;
        _retryButton.IsVisible = false;
        _closeButton.IsVisible = false;
    }

    private void Report(double fraction)
    {
        var ratio = Math.Clamp(fraction, 0, 1);
        _bar.IsIndeterminate = false;
        _bar.Value = ratio * 100;

        if (_expectedBytes > 0)
        {
            var received = (long)(ratio * _expectedBytes);
            _detailText.Text =
                $"{ByteText.FormatSize(received)} / {ByteText.FormatSize(_expectedBytes)}（{ratio * 100:0}%）";
        }
        else
        {
            _detailText.Text = $"{ratio * 100:0}%";
        }
    }

    private void Dismiss(bool cancelDownload)
    {
        if (cancelDownload)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经取消了：忽略
            }
        }

        // 失败态下点「取消」不存在；下载中关窗后由下载侧收到取消异常自行收尾，
        // 这里把重试等待一并放掉，调用方不会卡在 WaitForRetryAsync 上。
        _retryChoice?.TrySetResult(false);
        Close();
    }

    private void CompleteRetry(bool again)
    {
        if (again)
        {
            ShowRetrying();
        }

        _retryChoice?.TrySetResult(again);
    }

    private void Finish()
    {
        _retryChoice?.TrySetResult(false);

        // 只取消、不 Dispose：令牌还会被下载循环读一次（window.Token 在 while 里取），
        // Dispose 后再碰它就会抛 ObjectDisposedException。这个对象马上就会被回收，不值当为它冒险。
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略
        }
    }

    private static Button MakeButton(string content, bool primary) => new()
    {
        Content = content,
        MinWidth = 72,
        Padding = new Thickness(14, 7),
        CornerRadius = new CornerRadius(7),
        Cursor = new Cursor(StandardCursorType.Hand),
        Background = new SolidColorBrush(Color.Parse(primary ? "#2563EB" : "#FFFFFF")),
        Foreground = new SolidColorBrush(Color.Parse(primary ? "#FFFFFF" : "#111827")),
        BorderBrush = new SolidColorBrush(Color.Parse(primary ? "#2563EB" : "#D1D5DB")),
        BorderThickness = new Thickness(1),
        FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal
    };
}
