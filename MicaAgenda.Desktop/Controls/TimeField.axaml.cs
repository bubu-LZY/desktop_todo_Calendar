using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using MicaAgenda.App.Models;

namespace MicaAgenda.Desktop.Controls;

/// <summary>
/// 紧凑的「时:分」内联输入控件：两个两位小框直接展示时间，支持键入、滚轮与 ↑↓ 微调。
/// 对外只暴露一个可双向绑定的 <see cref="Time"/>（<see cref="TimeSpan"/>，与 TimePicker 同型），
/// null 时按任务默认时刻 09:00 显示（与添加任务表单「没选 = 9 点」的口径一致）。
/// </summary>
public partial class TimeField : UserControl
{
    public static readonly StyledProperty<TimeSpan?> TimeProperty =
        AvaloniaProperty.Register<TimeField, TimeSpan?>(
            nameof(Time),
            defaultValue: CalendarTask.DefaultTime.ToTimeSpan(),
            defaultBindingMode: BindingMode.TwoWay);

    public TimeSpan? Time
    {
        get => GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    private bool _syncing;

    public TimeField()
    {
        InitializeComponent();

        HourBox.AddHandler(TextInputEvent, OnHourTextInput, RoutingStrategies.Tunnel);
        MinuteBox.AddHandler(TextInputEvent, OnMinuteTextInput, RoutingStrategies.Tunnel);
        HourBox.GotFocus += (_, _) => HourBox.SelectAll();
        MinuteBox.GotFocus += (_, _) => MinuteBox.SelectAll();
        HourBox.LostFocus += (_, _) => CommitFromBoxes();
        MinuteBox.LostFocus += (_, _) => CommitFromBoxes();
        HourBox.KeyDown += OnHourKeyDown;
        MinuteBox.KeyDown += OnMinuteKeyDown;
        HourBox.PointerWheelChanged += OnHourWheel;
        MinuteBox.PointerWheelChanged += OnMinuteWheel;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateBoxes();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TimeProperty)
        {
            UpdateBoxes();
        }
    }

    private int CurrentHour
    {
        get => int.TryParse(HourBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            ? Math.Clamp(h, 0, 23)
            : CalendarTask.DefaultTime.Hour;
    }

    private int CurrentMinute
    {
        get => int.TryParse(MinuteBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            ? Math.Clamp(m, 0, 59)
            : CalendarTask.DefaultTime.Minute;
    }

    /// <summary>外部 <see cref="Time"/> 变了（或刚加载）时把两个小框刷成 HH / mm。</summary>
    private void UpdateBoxes()
    {
        if (_syncing)
        {
            return;
        }

        var t = Time ?? CalendarTask.DefaultTime.ToTimeSpan();
        var hour = Math.Clamp(t.Hours, 0, 23);
        var minute = Math.Clamp(t.Minutes, 0, 59);

        var hourText = hour.ToString("00", CultureInfo.InvariantCulture);
        var minuteText = minute.ToString("00", CultureInfo.InvariantCulture);
        if (HourBox.Text != hourText)
        {
            HourBox.Text = hourText;
        }

        if (MinuteBox.Text != minuteText)
        {
            MinuteBox.Text = minuteText;
        }
    }

    private void OnHourTextInput(object? sender, TextInputEventArgs e)
    {
        // 只允许数字进框：其余输入在隧道阶段拦掉，免得「9a」之类脏值还要在提交时兜底。
        // 允许空（用户先删完再输入的过程态），提交时再按 0 处理。
        var projected = ProjectText(HourBox, e.Text ?? string.Empty);
        if (!IsValidPartialNumber(projected, 0, 23))
        {
            e.Handled = true;
        }
    }

    private void OnMinuteTextInput(object? sender, TextInputEventArgs e)
    {
        var projected = ProjectText(MinuteBox, e.Text ?? string.Empty);
        if (!IsValidPartialNumber(projected, 0, 59))
        {
            e.Handled = true;
        }
    }

    /// <summary>模拟这次输入完成后框里会变成什么文本（要把选区替换考虑进去）。</summary>
    private static string ProjectText(TextBox box, string input)
    {
        var text = box.Text ?? string.Empty;
        var start = Math.Clamp(box.SelectionStart, 0, text.Length);
        var length = Math.Clamp(box.SelectionEnd - start, 0, text.Length - start);
        return text.Remove(start, length).Insert(start, input);
    }

    private static bool IsValidPartialNumber(string text, int min, int max)
    {
        if (text.Length == 0)
        {
            return true;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        // 一位前缀（如「7」）要放行，它可能正要输成「07 / 17」；两位就必须在合法区间内。
        return text.Length == 1 ? value <= 9 : value >= min && value <= max;
    }

    private void OnHourKeyDown(object? sender, KeyEventArgs e) => OnKey(e, HourBox, isHour: true);

    private void OnMinuteKeyDown(object? sender, KeyEventArgs e) => OnKey(e, MinuteBox, isHour: false);

    private void OnKey(KeyEventArgs e, TextBox box, bool isHour)
    {
        switch (e.Key)
        {
            case Key.Up:
                Step(box, isHour, +1, e.KeyModifiers);
                e.Handled = true;
                break;
            case Key.Down:
                Step(box, isHour, -1, e.KeyModifiers);
                e.Handled = true;
                break;
            case Key.Enter:
                CommitFromBoxes();
                box.Focus();
                box.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnHourWheel(object? sender, PointerWheelEventArgs e)
    {
        Step(HourBox, isHour: true, e.Delta.Y > 0 ? +1 : -1, e.KeyModifiers);
        e.Handled = true;
    }

    private void OnMinuteWheel(object? sender, PointerWheelEventArgs e)
    {
        Step(MinuteBox, isHour: false, e.Delta.Y > 0 ? +1 : -1, e.KeyModifiers);
        e.Handled = true;
    }

    /// <summary>在小时 / 分钟上加减一步（分钟步进 5，按住 Shift 步进 1），循环进位。</summary>
    private void Step(TextBox box, bool isHour, int direction, KeyModifiers modifiers)
    {
        var hour = CurrentHour;
        var minute = CurrentMinute;
        var step = isHour ? 1 : ((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift ? 1 : 5);

        if (isHour)
        {
            hour = (hour + direction * step + 24) % 24;
        }
        else
        {
            var total = hour * 60 + minute + direction * step;
            // 时刻在当天范围内循环，不向日期溢出 —— 任务时间就是一天内的时刻。
            total = (total + 24 * 60) % (24 * 60);
            hour = total / 60;
            minute = total % 60;
        }

        PushTime(hour, minute);
        if (isHour)
        {
            HourBox.Text = hour.ToString("00", CultureInfo.InvariantCulture);
        }
        else
        {
            MinuteBox.Text = minute.ToString("00", CultureInfo.InvariantCulture);
        }

        box.Focus();
        box.SelectAll();
    }

    private void CommitFromBoxes() => PushTime(CurrentHour, CurrentMinute);

    private void PushTime(int hour, int minute)
    {
        _syncing = true;
        try
        {
            SetCurrentValue(TimeProperty, new TimeSpan(hour, minute, 0));
        }
        finally
        {
            _syncing = false;
        }
    }
}
