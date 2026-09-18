using Avalonia.Controls;
using Avalonia.Interactivity;
using MicaAgenda.App.Services;

namespace MicaAgenda.Desktop;

/// <summary>AI 对话日志查看窗：只读展示最近 7 天的对话记录（更早的已按周自动清理）。</summary>
public partial class AiLogWindow : Window
{
    public AiLogWindow()
    {
        InitializeComponent();
        var text = AiChatLogService.ReadAll();
        LogBox.Text = string.IsNullOrWhiteSpace(text)
            ? "最近 7 天还没有 AI 对话记录。"
            : text;
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
