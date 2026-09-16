using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MicaAgenda.App.Helpers;

namespace MicaAgenda.Desktop.Controls;

/// <summary>
/// 提醒时间多选下拉。绑定一个字符串标签集合（<see cref="ReminderLeadCatalog.SelectableLabels"/>
/// 里的档位文案），控件在集合上原地增删；空集合 = 「不提醒」。
///
/// 用户首次把勾选从 0 项变成 ≥1 项时抛 <see cref="FirstLeadSelected"/>，
/// 宿主用来做「还没配推送通道」的提醒（见 ReminderGate），控件本身不碰配置。
/// </summary>
public partial class ReminderLeadPicker : UserControl
{
    /// <summary>当前勾选的档位标签集合（与 VM 共享同一个实例，原地增删）。</summary>
    public static readonly StyledProperty<IList<string>?> SelectedLabelsProperty =
        AvaloniaProperty.Register<ReminderLeadPicker, IList<string>?>(nameof(SelectedLabels));

    public IList<string>? SelectedLabels
    {
        get => GetValue(SelectedLabelsProperty);
        set => SetValue(SelectedLabelsProperty, value);
    }

    /// <summary>用户勾选使选中数「从 0 变 ≥1」时触发（参数无实际意义，仅作信号）。</summary>
    public event EventHandler? FirstLeadSelected;

    private bool _building;

    public ReminderLeadPicker()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        RebuildOptions();
        RefreshSummary();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedLabelsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldNotify)
            {
                oldNotify.CollectionChanged -= OnSelectedCollectionChanged;
            }

            if (change.NewValue is INotifyCollectionChanged newNotify)
            {
                newNotify.CollectionChanged += OnSelectedCollectionChanged;
            }

            RebuildOptions();
            RefreshSummary();
        }
    }

    private void OnSelectedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_building)
        {
            return;
        }

        SyncCheckBoxes();
        RefreshSummary();
    }

    /// <summary>按档位表重建勾选项；勾中状态以绑定集合为准。</summary>
    private void RebuildOptions()
    {
        _building = true;
        try
        {
            OptionsHost.Children.Clear();
            foreach (var label in ReminderLeadCatalog.SelectableLabels)
            {
                var check = new CheckBox
                {
                    Content = label,
                    Classes = { "option" },
                    IsChecked = SelectedLabels?.Contains(label) == true
                };
                check.Tag = label;
                check.IsCheckedChanged += OnOptionChecked;
                OptionsHost.Children.Add(check);
            }
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>外部把集合整个换掉 / 程序化清空时，把勾选态重新对齐。</summary>
    private void SyncCheckBoxes()
    {
        _building = true;
        try
        {
            foreach (var child in OptionsHost.Children)
            {
                if (child is CheckBox { Tag: string label } check)
                {
                    check.IsChecked = SelectedLabels?.Contains(label) == true;
                }
            }
        }
        finally
        {
            _building = false;
        }
    }

    private void OnOptionChecked(object? sender, RoutedEventArgs e)
    {
        if (_building || sender is not CheckBox { Tag: string label } check)
        {
            return;
        }

        var selected = SelectedLabels;
        if (selected is null)
        {
            // 没有可写的绑定集合时勾选无处落，直接回退 UI，不假装记住了。
            _building = true;
            check.IsChecked = false;
            _building = false;
            return;
        }

        var hadAny = selected.Count > 0;
        var wantsChecked = check.IsChecked == true;
        var isSelected = selected.Contains(label);

        if (wantsChecked && !isSelected)
        {
            selected.Add(label);
        }
        else if (!wantsChecked && isSelected)
        {
            selected.Remove(label);
        }

        RefreshSummary();

        if (!hadAny && selected.Count > 0)
        {
            FirstLeadSelected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshSummary()
    {
        if (SummaryText is null)
        {
            return;
        }

        var labels = SelectedLabels;
        SummaryText.Text = labels is null
            ? ReminderLeadCatalog.NoneLabel
            : ReminderLeadCatalog.Summarize(labels);
    }

    private void TogglePopup(object? sender, RoutedEventArgs e)
    {
        Popup.IsOpen = !Popup.IsOpen;
    }
}
