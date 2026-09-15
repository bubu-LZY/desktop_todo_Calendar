using System;
using System.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.Desktop;

/// <summary>
/// 「编辑任务」窗口：任务内容 / 任务时间 / 提醒时间一次改完。
///
/// 背景（用户上报的 bug）：右键菜单的「编辑」以前只是把标题就地换成输入框，
/// 任务时刻和提醒时间既没有控件也没有入口 —— 编辑时根本改不了这两项。
/// 现在统一走这个窗口，日期格子里的任务条、右侧今日任务都用同一个入口。
///
/// 只负责收集输入，写回数据由调用方（<see cref="MainWindow"/>）交给
/// <c>MainViewModel.UpdateTask</c> —— 窗口不碰数据层，和 SettingsWindow 同一套路数。
/// </summary>
public partial class TaskEditWindow : Window
{
    private static readonly string[] WeekdayNames = ["日", "一", "二", "三", "四", "五", "六"];

    private readonly AppConfig? _config;
    private bool _initializing;

    /// <summary>XAML 编译器要求的无参构造（仅供编译期/设计期）；运行时一律走有参构造。</summary>
    public TaskEditWindow()
    {
        InitializeComponent();
    }

    public TaskEditWindow(CalendarTask task, AppConfig? config) : this()
    {
        _config = config;

        // 填初始值期间不要触发「提醒送不出去」的提示，否则一开窗就会莫名黄一条
        _initializing = true;

        DateText.Text = $"{task.Date:yyyy年M月d日}　周{WeekdayNames[(int)task.Date.DayOfWeek]}";
        TitleBox.Text = task.Title;
        TimeBox.SelectedTime = (task.Time ?? CalendarTask.DefaultTime).ToTimeSpan();

        LeadBox.ItemsSource = ReminderLeadCatalog.Labels;
        LeadBox.SelectedItem = ReminderLeadCatalog.ToLabel(task.ReminderLeadMinutes);

        _initializing = false;

        // 标题框里回车 = 保存（标题是单行的，回车没有别的含义）
        TitleBox.KeyDown += OnTitleKeyDown;
        Opened += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>用户点了「保存」才是 true；取消 / 关窗都是 false。</summary>
    public bool Saved { get; private set; }

    /// <summary>编辑后的任务内容（调用方 Trim 后为空则视为不改标题）。</summary>
    public string EditedTitle => TitleBox.Text ?? string.Empty;

    /// <summary>编辑后的任务时刻；用户把 TimePicker 清空时返回 null（= 当天 9:00）。</summary>
    public TimeOnly? EditedTime
        => TimeBox.SelectedTime is { } span ? TimeOnly.FromTimeSpan(span) : null;

    /// <summary>编辑后的提醒档位：null = 不提醒，0 = 到时提醒。</summary>
    public int? EditedLeadMinutes => ReminderLeadCatalog.ToMinutes(LeadBox.SelectedItem as string);

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Finish(saved: true);
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Finish(saved: true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Finish(saved: false);

    private void Finish(bool saved)
    {
        Saved = saved;
        Close();
    }

    private void LeadBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        // 与快速添加表单同一个守门人：从「不提醒」切到真的提醒档、而当前没有任何推送通道时提示。
        // 这里用窗内黄字提示，不再另弹一个模态框（嵌套模态在桌面嵌入模式下容易点不动）。
        var shouldWarn = ReminderGate.ShouldWarnOnLeadChange(
            _config,
            FirstLabel(e.RemovedItems),
            FirstLabel(e.AddedItems));
        if (shouldWarn)
        {
            WarnText.Text = ReminderGate.FeishuMissingMessage;
        }

        WarnText.IsVisible = shouldWarn;
    }

    /// <summary>下拉的变更项里取出档位文案（Avalonia 给的 AddedItems/RemovedItems 是非泛型 IList）。</summary>
    private static string? FirstLabel(IList items) => items.Count > 0 ? items[0] as string : null;
}
