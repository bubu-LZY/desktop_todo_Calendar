using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Desktop;

public partial class StatisticsWindow : Window
{
    private CalendarData? _data;
    private object? _syncRoot;
    private bool _isMonthly;

    public StatisticsWindow()
    {
        InitializeComponent();
    }

    /// <summary>由宿主在 ShowDialog 前注入数据源（Avalonia XAML 要求无参构造）。</summary>
    public void Initialize(CalendarData data, object syncRoot)
    {
        _data = data;
        _syncRoot = syncRoot;
        WeekBtn.IsChecked = true;
        MonthBtn.IsChecked = false;
        Refresh();
    }

    private void Week_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isMonthly && WeekBtn.IsChecked != true)
        {
            WeekBtn.IsChecked = true;
            return;
        }

        _isMonthly = false;
        MonthBtn.IsChecked = false;
        WeekBtn.IsChecked = true;
        Refresh();
    }

    private void Month_Click(object? sender, RoutedEventArgs e)
    {
        if (_isMonthly && MonthBtn.IsChecked != true)
        {
            MonthBtn.IsChecked = true;
            return;
        }

        _isMonthly = true;
        WeekBtn.IsChecked = false;
        MonthBtn.IsChecked = true;
        Refresh();
    }

    private void Refresh()
    {
        var data = _data;
        var syncRoot = _syncRoot;
        if (data is null || syncRoot is null)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var periodStart = _isMonthly
            ? new DateOnly(today.Year, today.Month, 1)
            : ReportService.StartOfWeek(today);
        var report = TaskReportBuilder.Build(data, syncRoot, periodStart, today, _isMonthly);

        CompletionText.Text = $"完成率 {(report.DueCompletionRate * 100):F0}%";
        OverviewText.Text =
            $"期间到期 {report.DueCount} 项 · 已完成 {report.DueCompletedCount} 项" +
            $" · 新建 {report.CreatedCount} 项\n" +
            $"至今未完成 {report.OpenCount} 项 · 其中逾期 {report.OverdueCount} 项";
        AvgText.Text = report.AverageDuration is null
            ? "平均完成用时：暂无数据"
            : $"平均完成用时：{TimeText.FormatDuration(report.AverageDuration.Value)}";

        LongestText.Text = $"🐢 耗时最长（{report.Longest.Count}）";
        LongestList.Text = FormatLines(report.Longest, line =>
            $"{line.DurationText}{(line.LateDays > 0 ? $"（超时 {line.LateDays} 天）" : string.Empty)}");

        OpenTitle.Text = $"📌 至今未完成（{report.OpenCount}）";
        // 「拖了几天」读 ReportTaskLine.DelayText（按任务日期算，与创建时间无关），
        // 不在宿主里另拼一份 —— 口径重复就会漂移。
        OpenList.Text = FormatLines(report.StillOpen, line =>
            line.OverdueDays > 0 ? $"{line.DelayText}（计划 {line.Date:MM/dd}）" : line.DelayText);

        OverdueTitle.Text = $"🚨 逾期未完成（{report.OverdueCount}）";
        OverdueList.Text = FormatLines(report.Overdue, line => $"计划 {line.Date:MM/dd} · 逾期 {line.OverdueDays} 天");

        // 空态：没有未完成/逾期时给个占位，避免白茫茫一片
        if (report.StillOpen.Count == 0)
        {
            OpenList.Text = "暂无未完成任务。";
        }

        if (report.Overdue.Count == 0)
        {
            OverdueList.Text = "暂无逾期任务。";
        }
    }

    private static string FormatLines(System.Collections.Generic.List<ReportTaskLine> lines, Func<ReportTaskLine, string> detail)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n", lines.Select((line, index) => $"{index + 1}. {line.Title} — {detail(line)}"));
    }
}
