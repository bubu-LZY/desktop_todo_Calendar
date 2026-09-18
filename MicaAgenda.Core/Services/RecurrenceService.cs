using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 周期任务的物化展开：把一条「源任务」按重复规则生成后续的重复实例。
///
/// 设计取舍 —— 为什么物化而不是动态计算：
/// 物化把未来的重复实例提前生成为普通 <see cref="CalendarTask"/>（独立 Id、独立完成态），
/// 这样日历构建、编辑、勾选完成、删除、提醒、MCP 全部复用现有逻辑，一处都不用为"虚拟日期"打补丁。
/// 代价是数据文件里会多出未来的实例，所以这里封顶 2 年（约 730 天），
/// 需要更久的用 <see cref="CalendarTask.RecurrenceEnd"/> 显式控制。
/// </summary>
public static class RecurrenceService
{
    /// <summary>物化封顶天数：无结束日期的周期任务最多向前铺约 2 年。</summary>
    public const int MaxMaterializedDays = 730;

    /// <summary>
    /// 按模板生成后续实例（不含模板本身，模板自己就是第一次发生）。
    /// 从模板日期之后开始，直到 <paramref name="template"/> 的 <see cref="CalendarTask.RecurrenceEnd"/>
    /// （含）或物化封顶，先到先停。
    /// </summary>
    public static List<CalendarTask> Expand(CalendarTask template)
    {
        if (template.Recurrence == RecurrenceFrequency.None)
        {
            return [];
        }

        var interval = Math.Max(1, template.RecurrenceInterval);
        var cap = template.Date.AddDays(MaxMaterializedDays);
        var result = new List<CalendarTask>();
        var next = NextDate(template.Date, template.Recurrence, interval);

        while (next <= cap && (template.RecurrenceEnd is null || next <= template.RecurrenceEnd))
        {
            result.Add(CreateInstance(template, next));
            next = NextDate(next, template.Recurrence, interval);
        }

        return result;
    }

    /// <summary>按频率与间隔计算下一个发生日期（从 <paramref name="current"/> 起）。</summary>
    public static DateOnly NextDate(DateOnly current, RecurrenceFrequency frequency, int interval)
    {
        return frequency switch
        {
            RecurrenceFrequency.Daily => current.AddDays(interval),
            RecurrenceFrequency.Weekly => current.AddDays(interval * 7),
            RecurrenceFrequency.Monthly => current.AddMonths(interval),
            RecurrenceFrequency.Yearly => current.AddYears(interval),
            _ => current.AddDays(1),
        };
    }

    /// <summary>
    /// 由模板生成某一天的实例：标题 / 时刻 / 重要 / 提醒档位全部复制，
    /// Id 独立（各实例可单独勾选完成），<see cref="CalendarTask.SeriesId"/> 指向源任务。
    /// 完成态不复制（新的未来实例默认为未完成），提醒「已推」标记也不复制。
    /// </summary>
    private static CalendarTask CreateInstance(CalendarTask template, DateOnly date)
    {
        var instance = new CalendarTask
        {
            Date = date,
            Title = template.Title,
            Time = template.Time,
            IsImportant = template.IsImportant,
            CreatedAt = template.CreatedAt,
            SeriesId = template.Id,
        };

        // 提醒档位复制一份（多选提醒的额外档位是引用类型，必须深拷贝，否则与模板共享会被互相污染）。
        var leads = template.AllReminderLeads;
        instance.SetReminderLeads(leads.Count > 0 ? leads : null);
        return instance;
    }

    /// <summary>频率的中文描述（管理面板展示用）。</summary>
    public static string DescribeFrequency(RecurrenceFrequency frequency)
        => frequency switch
        {
            RecurrenceFrequency.Daily => "每天",
            RecurrenceFrequency.Weekly => "每周",
            RecurrenceFrequency.Monthly => "每月",
            RecurrenceFrequency.Yearly => "每年",
            _ => string.Empty
        };

    /// <summary>周期任务源任务的规则摘要，如「每周 · 间隔1 · 到 2026-12-31」。</summary>
    public static string DescribeRule(CalendarTask master)
    {
        var frequency = DescribeFrequency(master.Recurrence);
        var interval = master.RecurrenceInterval > 1 ? $" · 间隔 {master.RecurrenceInterval}" : string.Empty;
        var end = master.RecurrenceEnd is { } e ? $" · 到 {e:yyyy-MM-dd}" : string.Empty;
        return $"{frequency}{interval}{end}";
    }
}
