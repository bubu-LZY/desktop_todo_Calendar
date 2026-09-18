using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    /// <summary>
    /// 紧凑模式：按钮收窄（去掉 MinWidth、更小字号/内边距），用于「输入框一行排布」的
    /// 紧凑添加表单 —— 那里输入框要占掉大部分宽度，标准 112px 的下拉会把它挤扁。
    /// 弹出的档位列表不变。
    /// </summary>
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<ReminderLeadPicker, bool>(nameof(Compact));

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
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
        else if (change.Property == CompactProperty)
        {
            // 紧凑模式下把档位说明放进弹层（原来它独占添加表单的一行），
            // 按钮本身收窄成小图标（🔔）：隐藏摘要与箭头，只留一个固定宽度的图标按钮，
            // 否则「到时提醒、提前30分钟」这类长摘要会把输入框挤到只剩一点点。
            OptionsHint.IsVisible = Compact;
            ApplyCompactVisual();
            RefreshSummary();
        }
    }

    /// <summary>按 Compact 开关切换按钮的图标 / 摘要形态与尺寸。</summary>
    private void ApplyCompactVisual()
    {
        if (IconText is null || SummaryText is null || Arrow is null)
        {
            return;
        }

        IconText.IsVisible = Compact;
        SummaryText.IsVisible = !Compact;
        Arrow.IsVisible = !Compact;

        if (Compact)
        {
            RootButton.MinWidth = 0;
            RootButton.Width = 30;
            RootButton.Height = 22;
            RootButton.Padding = new Thickness(0);
            RootButton.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        }
        else
        {
            RootButton.MinWidth = 112;
            RootButton.Width = double.NaN;
            RootButton.Height = 24;
            RootButton.Padding = new Thickness(8, 0);
            RootButton.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left;
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

    /// <summary>按档位表重建勾选项；勾中状态以绑定集合为准。顶部多一个「不提醒」（与其他档位互斥）。</summary>
    private void RebuildOptions()
    {
        _building = true;
        try
        {
            OptionsHost.Children.Clear();

            var none = new CheckBox
            {
                Content = ReminderLeadCatalog.NoneLabel,
                Classes = { "option" },
                IsChecked = SelectedLabels?.Contains(ReminderLeadCatalog.NoneLabel) == true
            };
            none.Tag = ReminderLeadCatalog.NoneLabel;
            none.IsCheckedChanged += OnOptionChecked;
            OptionsHost.Children.Add(none);

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

        var wantsChecked = check.IsChecked == true;
        var isNone = string.Equals(label, ReminderLeadCatalog.NoneLabel, StringComparison.Ordinal);

        // 是否已有一个真正的提醒档位（「不提醒」不算）
        var hadRealLead = selected.Any(item => !string.Equals(item, ReminderLeadCatalog.NoneLabel, StringComparison.Ordinal));

        _building = true;
        try
        {
            if (wantsChecked)
            {
                if (isNone)
                {
                    // 勾「不提醒」：清空其他档位，只保留它（明确不提醒）
                    foreach (var other in selected.Where(item => !string.Equals(item, ReminderLeadCatalog.NoneLabel, StringComparison.Ordinal)).ToList())
                    {
                        selected.Remove(other);
                    }

                    if (!selected.Contains(ReminderLeadCatalog.NoneLabel))
                    {
                        selected.Add(ReminderLeadCatalog.NoneLabel);
                    }
                }
                else
                {
                    // 勾其他档位：移除「不提醒」，再添加该档位
                    if (selected.Contains(ReminderLeadCatalog.NoneLabel))
                    {
                        selected.Remove(ReminderLeadCatalog.NoneLabel);
                    }

                    if (!selected.Contains(label))
                    {
                        selected.Add(label);
                    }
                }
            }
            else
            {
                selected.Remove(label);
            }
        }
        finally
        {
            _building = false;
        }

        RefreshSummary();

        var hasRealLead = selected.Any(item => !string.Equals(item, ReminderLeadCatalog.NoneLabel, StringComparison.Ordinal));
        if (!hadRealLead && hasRealLead)
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

        // 紧凑（图标）模式下给「已选提醒」一个视觉反馈：选了档位图标变高亮色、没选（或不提醒）恢复灰。
        if (Compact && IconText is not null)
        {
            var hasRealLead = labels is not null
                && labels.Any(item => !string.Equals(item, ReminderLeadCatalog.NoneLabel, StringComparison.Ordinal));
            IconText.Foreground = hasRealLead ? Brushes.DodgerBlue : Brushes.Gray;
        }
    }

    private void TogglePopup(object? sender, RoutedEventArgs e)
    {
        Popup.IsOpen = !Popup.IsOpen;
    }
}
