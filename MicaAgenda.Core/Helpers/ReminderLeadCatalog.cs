namespace MicaAgenda.App.Helpers;

/// <summary>
/// 「提醒时间」下拉的固定档位。右侧面板与日期格子内的快速添加共用这一份，
/// 避免两处各写一份、慢慢跑偏。
///
/// 锚点是任务的时刻（任务当天 + 你在日期右边选的那个时间，没选就是当天 9:00）：
/// 选「提前30分钟」＝任务时刻往前 30 分钟推提醒；
/// 选「到时提醒」＝不往前推，任务时刻那一刻推（提前量 0 分钟）。
/// </summary>
public static class ReminderLeadCatalog
{
    /// <summary>「不提醒」：不设提前量，这条任务完全不推。</summary>
    public const string NoneLabel = "不提醒";

    /// <summary>「到时提醒」：不提前，任务时刻到点就推（提前量 0 分钟）。</summary>
    public const string OnTimeLabel = "到时提醒";

    // 提前量 0 有两个落点：「不提醒」存 null（完全不推），「到时提醒」存 0（到点推）。
    // 二者在数据上是 null 与 0 的区别，判断提醒时只在 ToMinutes 这一处收口。
    private static readonly (string Label, int Minutes)[] Table =
    [
        (NoneLabel, 0),
        (OnTimeLabel, 0),
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

    /// <summary>
    /// 把选项标签换算成提前量（分钟）。
    /// 「不提醒」返回 null（不推）；「到时提醒」返回 0（任务时刻那一刻推）；
    /// 其余档位返回各自的分钟数。无法识别的标签（含空串）也返回 null。
    /// </summary>
    public static int? ToMinutes(string? label)
    {
        foreach (var (text, minutes) in Table)
        {
            if (string.Equals(text, label, StringComparison.Ordinal))
            {
                return string.Equals(text, NoneLabel, StringComparison.Ordinal) ? null : minutes;
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="ToMinutes"/> 的反查：把存下来的提前量还原成下拉标签。
    /// null →「不提醒」、0 →「到时提醒」。
    ///
    /// 表里没有的自定义值（老数据 / MCP 写进来的）取「不超过它的最大档位」，
    /// 全都超过就退回「到时提醒」——保证反查结果一定落在下拉表内，编辑框不会出现空选项。
    /// </summary>
    public static string ToLabel(int? minutes)
    {
        if (minutes is null)
        {
            return NoneLabel;
        }

        if (minutes.Value <= 0)
        {
            return OnTimeLabel;
        }

        var best = OnTimeLabel;
        var bestMinutes = 0;
        foreach (var (label, value) in Table)
        {
            if (value <= minutes.Value && value > bestMinutes)
            {
                best = label;
                bestMinutes = value;
            }
        }

        return best;
    }
}
