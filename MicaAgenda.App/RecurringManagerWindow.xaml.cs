using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;
using MicaAgenda.App.ViewModels;

namespace MicaAgenda.App;

/// <summary>
/// 周期任务管理窗口：列出所有周期任务系列（源任务），支持添加、编辑（删旧建新）、删除单个、删除全部。
/// 数据直接操作 <see cref="MainViewModel"/>，改动会置 IsDirty 触发宿主的防抖自动保存。
/// </summary>
public partial class RecurringManagerWindow : Window
{
    private readonly MainViewModel _viewModel;

    public RecurringManagerWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        Refresh();
    }

    private void Refresh()
    {
        ListHost.Children.Clear();
        var masters = _viewModel.GetAllRecurringMasters();
        if (masters.Count == 0)
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = "还没有周期任务。点下方「添加周期任务」创建。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }

        foreach (var master in masters)
        {
            ListHost.Children.Add(BuildRow(master));
        }
    }

    private FrameworkElement BuildRow(CalendarTask master)
    {
        var title = new TextBlock
        {
            Text = master.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            TextWrapping = TextWrapping.Wrap
        };

        var reminderText = ReminderLeadCatalog.Summarize(ReminderLeadCatalog.ToLabels(master.AllReminderLeads));
        var detail = new TextBlock
        {
            Text = $"{RecurrenceService.DescribeRule(master)} · 从 {master.Date:yyyy-MM-dd} 起 · 提醒：{reminderText}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var edit = new Button { Content = "编辑", Padding = new Thickness(10, 3, 10, 3), FontSize = 11 };
        edit.Click += (_, _) => Edit(master);

        var delete = new Button
        {
            Content = "删除",
            Padding = new Thickness(10, 3, 10, 3),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))
        };
        delete.Click += (_, _) => Delete(master);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(edit);
        buttons.Children.Add(delete);

        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        left.Children.Add(title);
        left.Children.Add(detail);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(left);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xF0, 0xF3)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RecurringTaskWindow { Owner = this, DefaultDate = DateOnly.FromDateTime(DateTime.Now) };
        if (dialog.ShowDialog() != true || dialog.Input is not { } input)
        {
            return;
        }

        _viewModel.AddRecurringTask(
            dialog.DefaultDate, input.Title, input.Frequency, input.Interval, input.End, input.Time, input.ReminderLeads);
        Refresh();
    }

    private void Edit(CalendarTask master)
    {
        var dialog = new RecurringTaskWindow { Owner = this };
        dialog.InitializeForEdit(master);
        if (dialog.ShowDialog() != true || dialog.Input is not { } input)
        {
            return;
        }

        // 周期任务是物化的，编辑 = 删旧系列 + 用新参数重建；起始日期保持不变。
        var start = master.Date;
        _viewModel.DeleteRecurringSeries(master.Id);
        _viewModel.AddRecurringTask(start, input.Title, input.Frequency, input.Interval, input.End, input.Time, input.ReminderLeads);
        Refresh();
    }

    private void Delete(CalendarTask master)
    {
        if (MessageBox.Show(this, $"删除「{master.Title}」的整个周期任务系列（含未来实例）？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        _viewModel.DeleteRecurringSeries(master.Id);
        Refresh();
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.GetAllRecurringMasters().Count == 0)
        {
            return;
        }

        if (MessageBox.Show(this, "删除所有周期任务系列（含各自的未来实例）？此操作不可撤销。", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        _viewModel.DeleteAllRecurringSeries();
        Refresh();
    }
}
