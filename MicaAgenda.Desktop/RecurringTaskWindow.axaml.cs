using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.Desktop;

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
    private readonly ObservableCollection<string> _leadLabels = [];

    /// <summary>任务起始日期（由宿主在打开时写入，供创建时用）。</summary>
    public DateOnly DefaultDate { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>编辑模式：非 null 表示正在编辑这个周期任务源任务，确认后宿主会删旧建新。</summary>
    public Guid? EditMasterId { get; private set; }

    public RecurringTaskWindow()
    {
        InitializeComponent();
        ReminderBox.SelectedLabels = _leadLabels;
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
            ? new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue))
            : null;
        TimeBox.Time = master.Time?.ToTimeSpan();

        _leadLabels.Clear();
        foreach (var label in ReminderLeadCatalog.ToLabels(master.AllReminderLeads))
        {
            _leadLabels.Add(label);
        }
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
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
            ? DateOnly.FromDateTime(dt.Date)
            : (DateOnly?)null;

        // TimeField 的 Time 是 TimeSpan?；null 或正好 09:00 都按"没设具体时间"落 null（与添加普通任务同口径）。
        var time = TimeBox.Time is { } ts && ts != CalendarTask.DefaultTime.ToTimeSpan()
            ? TimeOnly.FromTimeSpan(ts)
            : (TimeOnly?)null;

        // 含「不提醒」→ 空列表；空（未指定）→ null（默认提前15分钟）；否则档位列表。
        var leads = ReminderLeadCatalog.ToCommitLeads(_leadLabels);

        Input = new RecurringTaskInput(title, frequency, interval, end, time, leads);
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
