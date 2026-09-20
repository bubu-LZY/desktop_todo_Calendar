using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Desktop.Controls;

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
    private Control? _thinkingBubble;

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

    private async void Send_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await SendAsync();

    /// <summary>打开 AI 对话日志查看窗口（最近 7 天，更早的已自动清理）。</summary>
    private async void OpenLog_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            var logWindow = new AiLogWindow();
            if (owner is not null)
            {
                await logWindow.ShowDialog(owner);
            }
            else
            {
                logWindow.Show();
            }
        }
        catch (Exception ex)
        {
            AddBubble("打开日志失败：" + ex.Message, isUser: false);
        }
    }

    private async void InputBox_KeyDown(object? sender, KeyEventArgs e)
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
    /// 等待回复期间的状态。**刻意不用 <c>IsEnabled = false</c>**：
    /// 窗口在「磨砂深色 / 石墨」背景模式下会把 Fluent 主题切成 Dark，而 Dark 的 disabled 画刷
    /// 都是半透明白（ButtonBackgroundDisabled=#33ffffff、ButtonForegroundDisabled=#66ffffff、
    /// TextControlBackgroundDisabled=#33ffffff），画在本卡片的白色底上等于完全透明 ——
    /// 表现就是"发完消息、等回复时输入框和发送按钮凭空消失"。
    /// 这里改成只换文案/配色 + 屏蔽点击 + 输入框只读：控件始终按正常状态渲染，一定看得见。
    /// </summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;

        SendButton.Content = busy ? "…" : "发送";
        SendButton.Background = new SolidColorBrush(Color.Parse(busy ? "#93B4F0" : "#2563EB"));
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
    private void RemoveBubble(Control? bubble)
    {
        if (bubble is not null)
        {
            ChatHost.Children.Remove(bubble);
        }
    }

    /// <summary>在反馈区追加一条气泡：用户右对齐蓝底，AI 左对齐浅灰底。返回该气泡，便于稍后移除。</summary>
    private Control AddBubble(string text, bool isUser)
    {
        var textBlock = new TextBlock
        {
            // 模型回复常带 Markdown 记号，而 TextBlock 不会渲染它 —— 不转换的话
            // 用户看到的就是「**重点**」「## 标题」这种原样记号。这里统一清成可读文本。
            // 用户自己输入的内容不动：那是他自己写的，改了反而奇怪。
            Text = isUser ? text : MarkdownText.ToPlainText(text),
            TextWrapping = TextWrapping.Wrap,
            // 12 → 10.5：用户反馈"字有点大，想看到更多对话"。字号小了之后单行能放更多字，
            // 所以 MaxWidth 同步放宽，否则白白浪费面板右侧的空白。
            FontSize = 10.5,
            MaxWidth = 250
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            // 内边距 / 行距也一起收紧 —— 只缩字号的话，气泡之间的空白仍是大字号的尺度，
            // "一屏看到更多轮"的效果会被吃掉一半。
            Padding = new Thickness(7, 4),
            Margin = new Thickness(0, 2),
            Child = textBlock
        };

        if (isUser)
        {
            border.Background = new SolidColorBrush(Color.Parse("#2563EB"));
            textBlock.Foreground = Brushes.White;
        }
        else
        {
            border.Background = new SolidColorBrush(Color.Parse("#F5F7FA"));
            textBlock.Foreground = new SolidColorBrush(Color.Parse("#111827"));
        }

        var wrapper = new StackPanel
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Children = { border }
        };

        ChatHost.Children.Add(wrapper);
        ChatScroll.ScrollToEnd();
        return wrapper;
    }
}
