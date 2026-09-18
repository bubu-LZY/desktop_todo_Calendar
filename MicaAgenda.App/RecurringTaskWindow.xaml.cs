using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.App;

/// <summary>「添加周期任务」小面板的输入结果（确定后由宿主读取）。</summary>
public sealed record RecurringTaskInput(
    string Title,
    RecurrenceFrequency Frequency,
    int Interval,
    DateOnly? End,
    TimeOnly? Time,
    IReadOnlyList<int>? ReminderLeads);

public partial class RecurringTaskWindow : Window
{
    /// <summary>任务起始日期（由宿主在打开时写入）。</summary>
    public DateOnly DefaultDate { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>编辑模式：非 null 表示正在编辑这个周期任务源任务，确认后宿主会删旧建新。</summary>
    public Guid? EditMasterId { get; private set; }

    public RecurringTaskWindow()
    {
        InitializeComponent();
        BuildReminderOptions();
    }

    /// <summary>用户点「确定」后的输入；取消 / 未确认时是 null。</summary>
    public RecurringTaskInput? Input { get; private set; }

    /// <summary>预填为「编辑现有周期任务」模式。</summary>
    public void InitializeForEdit(CalendarTask master)
    {
        EditMasterId = master.Id;
        Title = "编辑周期任务";

        TitleBox.Text = master.Title;
        FrequencyBox.SelectedIndex = master.Recurrence switch
        {
            RecurrenceFrequency.Weekly => 1,
            RecurrenceFrequency.Monthly => 2,
            RecurrenceFrequency.Yearly => 3,
            _ => 0
        };
        IntervalBox.Text = master.RecurrenceInterval.ToString();
        EndDatePicker.SelectedDate = master.RecurrenceEnd is { } end
            ? end.ToDateTime(TimeOnly.MinValue)
            : null;
        TimeBox.Text = master.Time?.ToString("HH:mm") ?? string.Empty;

        var labels = ReminderLeadCatalog.ToLabels(master.AllReminderLeads);
        foreach (var box in ReminderHost.Children.OfType<CheckBox>())
        {
            var label = box.Content?.ToString() ?? string.Empty;
            box.IsChecked = labels.Contains(label);
        }
    }

    /// <summary>
    /// 动态铺出提醒档位勾选框（与 Avalonia 的 ReminderLeadPicker 同一份档位表），
    /// 顶部多一个「不提醒」（与其他档位互斥）。
    /// </summary>
    private void BuildReminderOptions()
    {
        var none = CreateOption(ReminderLeadCatalog.NoneLabel);
        none.Checked += (_, _) => ClearExcept(ReminderLeadCatalog.NoneLabel);
        ReminderHost.Children.Add(none);

        foreach (var label in ReminderLeadCatalog.SelectableLabels)
        {
            var box = CreateOption(label);
            box.Checked += (_, _) => ClearExcept(label);
            ReminderHost.Children.Add(box);
        }
    }

    private CheckBox CreateOption(string label) => new()
    {
        Content = label,
        Margin = new Thickness(0, 0, 14, 4),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    /// <summary>勾选某档位时，取消勾选其余档位（「不提醒」与档位、档位之间互斥）。</summary>
    private void ClearExcept(string keep)
    {
        foreach (var child in ReminderHost.Children.OfType<CheckBox>())
        {
            if ((child.Content?.ToString() ?? string.Empty) != keep)
            {
                child.IsChecked = false;
            }
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            TitleBox.Focus();
            TitleBox.CaretIndex = TitleBox.Text?.Length ?? 0;
            return;
        }

        var frequency = FrequencyBox.SelectedIndex switch
        {
            1 => RecurrenceFrequency.Weekly,
            2 => RecurrenceFrequency.Monthly,
            3 => RecurrenceFrequency.Yearly,
            _ => RecurrenceFrequency.Daily,
        };

        var interval = int.TryParse(IntervalBox.Text?.Trim(), out var n) ? Math.Max(1, n) : 1;

        var end = EndDatePicker.SelectedDate is { } dt
            ? DateOnly.FromDateTime(dt)
            : (DateOnly?)null;

        // WPF 宿主当前没有时间/提醒的图形控件，这里用文本框解析 HH:mm；空 = 不设具体时间。
        TimeOnly? time = null;
        var timeText = TimeBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(timeText)
            && TimeOnly.TryParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = parsed == CalendarTask.DefaultTime ? null : parsed;
        }

        // 收集勾选的提醒档位：含「不提醒」→ 空列表；空 → null（默认提前15分钟）；否则档位。
        var labels = ReminderHost.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true)
            .Select(box => box.Content?.ToString() ?? string.Empty)
            .ToList();
        var leads = ReminderLeadCatalog.ToCommitLeads(labels);

        Input = new RecurringTaskInput(title, frequency, interval, end, time, leads);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
