using System;
using System.Collections.ObjectModel;
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

    /// <summary>XAML 编译器要求的无参构造（仅供编译期/设计期）；运行时一律走有参构造。</summary>
    public TaskEditWindow()
    {
        InitializeComponent();
    }

    public TaskEditWindow(CalendarTask task, AppConfig? config) : this()
    {
        _config = config;

        DateText.Text = $"{task.Date:yyyy年M月d日}　周{WeekdayNames[(int)task.Date.DayOfWeek]}";
        TitleBox.Text = task.Title;
        TimeBox.Time = (task.Time ?? CalendarTask.DefaultTime).ToTimeSpan();

        // 多选提醒：把任务现存的全部档位还原成勾选项。
        LeadBox.SelectedLabels = new ObservableCollection<string>(
            ReminderLeadCatalog.ToLabels(task.AllReminderLeads));

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

    /// <summary>编辑后的任务时刻；TimeField 为空时按默认 9:00（它本身不允许清空）。</summary>
    public TimeOnly? EditedTime
        => TimeBox.Time is { } span ? TimeOnly.FromTimeSpan(span) : null;

    /// <summary>编辑后的全部提醒提前量（分钟）；空集合 = 不提醒，0 = 到时提醒。</summary>
    public IReadOnlyList<int> EditedLeads
        => ReminderLeadCatalog.ToMinutesList(LeadBox.SelectedLabels);

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

    /// <summary>
    /// 第一次勾上任一档位、而当前没有任何推送通道时，用窗内黄字提示（不再弹模态框，
    /// 嵌套模态在桌面嵌入模式下容易点不动）。事件语义上触发时一定是「从无到有」。
    /// </summary>
    private void LeadBox_FirstSelected(object? sender, EventArgs e)
    {
        var shouldWarn = ReminderGate.ShouldWarnOnLeadToggle(_config, hadAny: false, hasAny: true);
        WarnText.Text = ReminderGate.FeishuMissingMessage;
        WarnText.IsVisible = shouldWarn;
    }
}
