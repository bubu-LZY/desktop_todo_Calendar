namespace MicaAgenda.App.Helpers;

/// <summary>
/// 「提醒时间」下拉的固定档位。右侧面板与日期格子内的快速添加共用这一份，
/// 避免两处各写一份、慢慢跑偏。
///
/// 锚点是任务当天的 9:00（<see cref="Models.CalendarTask.DefaultTime"/>）：
/// 选「提前30分钟」＝当天 08:30 推提醒。
/// </summary>
public static class ReminderLeadCatalog
{
    /// <summary>「不提醒」：不设提前量，这条任务完全不推。</summary>
    public const string NoneLabel = "不提醒";

    private static readonly (string Label, int Minutes)[] Table =
    [
        (NoneLabel, 0),
        ("提前3分钟", 3),
        ("提前5分钟", 5),
        ("提前10分钟", 10),
        ("提前15分钟", 15),
        ("提前30分钟", 30),
        ("提前1个小时", 60),
        ("提前3个小时", 180)
    ];

    /// <summary>下拉的全部选项，顺序即界面顺序。</summary>
    public static IReadOnlyList<string> Labels { get; } = Table.Select(item => item.Label).ToList();

    /// <summary>把选项标签换算成提前量（分钟）；「不提醒」或无法识别返回 null。</summary>
    public static int? ToMinutes(string? label)
    {
        foreach (var (text, minutes) in Table)
        {
            if (string.Equals(text, label, StringComparison.Ordinal))
            {
                return minutes > 0 ? minutes : null;
            }
        }

        return null;
    }
}
