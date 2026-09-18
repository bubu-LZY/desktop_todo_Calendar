using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.App;

/// <summary>
/// 迷你 AI 对话面板（内嵌在右上角，非弹窗）：输入框回车发送，反馈气泡显示在上方。
/// 内部持有一个 <see cref="AiAgentService"/>，走 function calling 复用 MCP 的同一套任务工具。
/// </summary>
public partial class AiChatPanel : UserControl
{
    private AiAgentService? _agent;
    private Action? _onDataChanged;
    private readonly List<AiChatMessage> _history = [];
    private bool _busy;
    private Border? _thinkingBubble;

    public AiChatPanel()
    {
        InitializeComponent();
    }

    public void Initialize(CalendarData data, object syncRoot, Func<AppConfig> configProvider, Action onDataChanged, McpHostActions? hostActions = null)
    {
        _agent = new AiAgentService(data, syncRoot, configProvider, onDataChanged, hostActions);
        _onDataChanged = onDataChanged;

        // 系统提示（含当前日期注入）由 AiAgentService.ChatAsync 统一构建，这里只放欢迎气泡。
        AddBubble("你好，可以让我管理任务，例如「明天下午3点开会」「这周有哪些任务」。", isUser: false);
    }

    /// <summary>把键盘焦点落到输入框：Popup 是独立窗口，打开后不会自动聚焦，需宿主在 Opened 里调一次。</summary>
    public void FocusInput()
    {
        InputBox.Focus();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    /// <summary>打开 AI 对话日志查看窗口（最近 7 天，更早的已自动清理）。</summary>
    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logWindow = new AiLogWindow { Owner = Window.GetWindow(this) };
            logWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            AddBubble("打开日志失败：" + ex.Message, isUser: false);
        }
    }

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async System.Threading.Tasks.Task SendAsync()
    {
        var text = InputBox.Text?.Trim();
        if (_busy || _agent is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        InputBox.Text = string.Empty;
        AddBubble(text, isUser: true);
        _history.Add(new AiChatMessage("user", text));
        SetBusy(true);

        try
        {
            var reply = await _agent.ChatAsync(_history);
            _history.Add(new AiChatMessage("assistant", reply));
            AddBubble(reply, isUser: false);

            // 模型可能通过工具改了任务，通知宿主刷新并落盘。
            _onDataChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AddBubble("出错了：" + ex.Message, isUser: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 等待回复期间的状态。刻意不用 <c>IsEnabled = false</c>（与 Avalonia 宿主同一处理）：
    /// 禁用态的控件会变成"看不见的灰"，用户实测发完消息、等回复时输入框和发送按钮会消失。
    /// 这里只换文案/配色 + 屏蔽点击 + 输入框只读，控件始终保持正常可见。
    /// </summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;

        SendButton.Content = busy ? "…" : "发送";
        SendButton.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(busy ? "#93B4F0" : "#2563EB"));
        SendButton.IsHitTestVisible = !busy;
        InputBox.IsReadOnly = busy;

        if (busy)
        {
            _thinkingBubble = AddBubble("思考中…", isUser: false);
        }
        else
        {
            RemoveBubble(_thinkingBubble);
            _thinkingBubble = null;
        }
    }

    /// <summary>移除一条气泡（用于撤掉"思考中…"占位）。</summary>
    private void RemoveBubble(Border? bubble)
    {
        if (bubble is not null)
        {
            ChatHost.Children.Remove(bubble);
        }
    }

    /// <summary>在反馈区追加一条气泡：用户右对齐蓝底，AI 左对齐浅灰底。返回该气泡，便于稍后移除。</summary>
    private Border AddBubble(string text, bool isUser)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            MaxWidth = 230
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 3, 0, 3),
            Child = textBlock
        };

        if (isUser)
        {
            border.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            textBlock.Foreground = Brushes.White;
        }
        else
        {
            border.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA));
            textBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
        }

        border.HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        ChatHost.Children.Add(border);
        ChatScroll.ScrollToEnd();
        return border;
    }
}
