namespace MicaAgenda.App.Models;

/// <summary>
/// 「用户在日历里删掉了这条复习任务」的待同步记录。
///
/// 为什么需要它：对端（my-mindmap agent）是复习任务「存在与否」的权威，日历里删掉的复习任务
/// 会在下一次同步时按对端计划重新建出来 —— 用户看到的现象就是「删不掉」。要把删除真正通知到
/// 对端，就必须记住这件事直到对端确认：对端没开（或网络不通）时如果直接忘掉，
/// 几分钟后的下一次同步就会把它重新建回来。
///
/// 记录用「日期 + 去前缀标题」做键，与两端配对用的键完全一致。
/// </summary>
public sealed class ReviewDeletion
{
    public DateOnly Date { get; set; }

    /// <summary>去前缀后的标题（配对键的另一半）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>记录产生时间，仅用于排查。</summary>
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.Now;
}
