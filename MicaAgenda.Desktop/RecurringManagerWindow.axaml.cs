using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;
using MicaAgenda.App.ViewModels;

namespace MicaAgenda.Desktop;

/// <summary>
/// 周期任务管理窗口：列出所有周期任务系列（源任务），支持添加、编辑（删旧建新）、删除单个、删除全部。
/// 数据直接操作 <see cref="MainViewModel"/>，改动会置 IsDirty 触发宿主的防抖自动保存。
/// </summary>
public partial class RecurringManagerWindow : Window
{
    private MainViewModel? _viewModel;

    public RecurringManagerWindow()
    {
        InitializeComponent();
    }

    public void Initialize(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        Refresh();
    }

    private void Refresh()
    {
        if (_viewModel is null || ListHost is null)
        {
            return;
        }

        ListHost.Children.Clear();
        var masters = _viewModel.GetAllRecurringMasters();
        if (masters.Count == 0)
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = "还没有周期任务。点下方「添加周期任务」创建。",
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                FontSize = 12,
                Margin = new Thickness(0, 8)
            });
            return;
        }

        foreach (var master in masters)
        {
            ListHost.Children.Add(BuildRow(master));
        }
    }

    private Control BuildRow(CalendarTask master)
    {
        var title = new TextBlock
        {
            Text = master.Title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#111827")),
            TextWrapping = TextWrapping.Wrap
        };

        var reminderText = ReminderLeadCatalog.Summarize(ReminderLeadCatalog.ToLabels(master.AllReminderLeads));
        var detail = new TextBlock
        {
            Text = $"{RecurrenceService.DescribeRule(master)} · 从 {master.Date:yyyy-MM-dd} 起 · 提醒：{reminderText}",
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var edit = new Button { Content = "编辑", Padding = new Thickness(10, 3), FontSize = 11 };
        edit.Click += (_, _) => Edit(master);

        var delete = new Button
        {
            Content = "删除",
            Padding = new Thickness(10, 3),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#DC2626"))
        };
        delete.Click += (_, _) => Delete(master);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(edit);
        buttons.Children.Add(delete);

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(title);
        left.Children.Add(detail);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(left);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#EDF0F3")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Child = grid
        };
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var dialog = new RecurringTaskWindow { DefaultDate = DateOnly.FromDateTime(DateTime.Now) };
        await dialog.ShowDialog(this);
        if (dialog.Input is not { } input)
        {
            return;
        }

        _viewModel.AddRecurringTask(
            dialog.DefaultDate, input.Title, input.Frequency, input.Interval, input.End, input.Time, input.ReminderLeads);
        Refresh();
    }

    private async void Edit(CalendarTask master)
    {
        if (_viewModel is null)
        {
            return;
        }

        var dialog = new RecurringTaskWindow();
        dialog.InitializeForEdit(master);
        await dialog.ShowDialog(this);
        if (dialog.Input is not { } input)
        {
            return;
        }

        // 周期任务是物化的（源任务 + 未来实例），编辑 = 删旧系列 + 用新参数重建；起始日期保持不变。
        var start = master.Date;
        _viewModel.DeleteRecurringSeries(master.Id);
        _viewModel.AddRecurringTask(start, input.Title, input.Frequency, input.Interval, input.End, input.Time, input.ReminderLeads);
        Refresh();
    }

    private async void Delete(CalendarTask master)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!await ConfirmAsync($"删除「{master.Title}」的整个周期任务系列（含未来实例）？"))
        {
            return;
        }

        _viewModel.DeleteRecurringSeries(master.Id);
        Refresh();
    }

    private async void DeleteAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.GetAllRecurringMasters().Count == 0)
        {
            return;
        }

        if (!await ConfirmAsync("删除所有周期任务系列（含各自的未来实例）？此操作不可撤销。"))
        {
            return;
        }

        _viewModel.DeleteAllRecurringSeries();
        Refresh();
    }

    private async System.Threading.Tasks.Task<bool> ConfirmAsync(string message)
    {
        var result = false;
        var yes = new Button { Content = "确定", MinWidth = 72 };
        var no = new Button { Content = "取消", MinWidth = 72 };
        var win = new Window
        {
            Title = "确认",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { yes, no }
                    }
                }
            }
        };

        yes.Click += (_, _) => { result = true; win.Close(); };
        no.Click += (_, _) => { result = false; win.Close(); };
        await win.ShowDialog(this);
        return result;
    }
}
