namespace MicaAgenda.App.Models;

/// <summary>
/// 「思维导图复习同步」的任务标题约定（桌面日历与 my-mindmap agent 两边一致）：
/// 本程序写进日历的复习任务带 <see cref="Prefix"/>，历史上用过 <see cref="LegacyPrefix"/>，
/// 识别与配对时两个前缀都要认。
/// 用户自己建的任务（标题没有前缀）一律不参与同步。
/// </summary>
public static class ReviewTaskTitle
{
    /// <summary>新版复习任务标题前缀（由本程序写入日历）。</summary>
    public const string Prefix = "[MM复习]";

    /// <summary>旧版前缀，只读识别用：历史上推送过的任务标题带这个前缀，也要能配对与清理。</summary>
    public const string LegacyPrefix = "[复习]";

    /// <summary>标题是否属于「复习计划同步」管辖范围。</summary>
    public static bool IsReview(string? title) =>
        title is not null
        && (title.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || title.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase));
}