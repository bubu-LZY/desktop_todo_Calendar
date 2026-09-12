using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 与 my-mindmap agent 复习计划同步的「纯逻辑」：只做比对与仲裁，不碰网络、线程与落盘，
/// 因此可以被单测完整覆盖；真正的 HTTP 调用与数据写入放在 MindMapReviewSyncService。
///
/// 语义约定（两个程序一致）：
/// - 对端（my-mindmap agent）是复习任务「存在与否」的唯一权威：计划里已经没有的复习任务要清掉；
/// - 状态（完成 / 未完成）以「状态最后变更时间」较新的一方为准（LWW）。
/// </summary>
internal static class ReviewSyncPlanner
{
    /// <summary>新版复习任务标题前缀（由本程序写进日历）。</summary>
    internal const string ReviewPrefix = "[MM复习]";

    /// <summary>旧版前缀，只读识别用：历史上推送过的任务标题带这个前缀，也要能配对与清理。</summary>
    internal const string LegacyPrefix = "[复习]";

    /// <summary>对端没给出标题时的兜底标题。</summary>
    internal const string FallbackTitle = "复习任务";

    /// <summary>标题是否属于「复习计划同步」管辖范围。用户自己建的任务一律不参与同步。</summary>
    internal static bool HasReviewPrefix(string? title) =>
        title is not null
        && (title.StartsWith(ReviewPrefix, StringComparison.OrdinalIgnoreCase)
            || title.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>去掉前缀后的标题，两端用它做「日期 + 标题」配对。</summary>
    internal static string NormalizeTitle(string? title)
    {
        var t = title?.Trim() ?? string.Empty;
        if (t.StartsWith(ReviewPrefix, StringComparison.OrdinalIgnoreCase))
        {
            t = t[ReviewPrefix.Length..].Trim();
        }
        else if (t.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            t = t[LegacyPrefix.Length..].Trim();
        }

        return t;
    }

    /// <summary>把对端给的无前缀标题拼成本程序使用的日历任务标题。</summary>
    internal static string BuildTaskTitle(string? remoteTitle)
    {
        var normalized = NormalizeTitle(remoteTitle);
        return ReviewPrefix + (normalized.Length == 0 ? FallbackTitle : normalized);
    }

    /// <summary>配对键：日期 + 规范化标题。</summary>
    internal static string KeyOf(DateOnly date, string? title) =>
        $"{date:yyyy-MM-dd}::{NormalizeTitle(title)}";

    /// <summary>
    /// 比对对端复习计划与本地复习任务，产出本次需要执行的动作。
    /// </summary>
    /// <param name="entries">对端复习计划快照。</param>
    /// <param name="localTasks">本地所有带复习前缀的任务（调用方必须先按前缀过滤）。</param>
    /// <param name="now">对端没给时间戳时用于兜底的时间基准。</param>
    internal static ReviewSyncPlan Build(
        IReadOnlyList<ReviewSyncEntry> entries,
        IReadOnlyList<CalendarTask> localTasks,
        DateTimeOffset now)
    {
        var creates = new List<ReviewSyncCreate>();
        var pulls = new List<ReviewSyncStatusApply>();
        var pushes = new List<ReviewSyncPush>();
        var deletes = new List<ReviewSyncDelete>();

        // 本轮新建的任务也放进候选集：同一天同名的多条复习周期（同名字节点、多个周期撞到同一天）
        // 只应该对应日历里的一条任务，不能各建一条。
        var working = localTasks.ToList();

        // 计划里出现过的配对键，用于判断本地哪些复习任务是「对端已经没有」的孤儿。
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal);

        // 已经配对上的本地任务 id（含被判定为重复、本次要删掉的那些），避免它们再被当成孤儿记一次。
        var matchedIds = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            var normalized = NormalizeTitle(entry.Title);
            if (normalized.Length == 0)
            {
                normalized = FallbackTitle;
            }

            expectedKeys.Add($"{entry.Date:yyyy-MM-dd}::{normalized}");

            // 同一天同一标题可能有多条（历史版本重复推送留下的）。
            // 选一条正主（优先新前缀、其次最早创建），其余本次清掉，避免日历里越攒越多。
            var candidates = working
                .Where(t => t.Date == entry.Date && NormalizeTitle(t.Title) == normalized)
                .OrderBy(PrefixRank)
                .ThenBy(t => t.CreatedAt)
                .ThenBy(t => t.Id)
                .ToList();

            if (candidates.Count == 0)
            {
                // 本地还没有这条复习任务：新建，并直接带上对端的状态与时间戳。
                var at = entry.StatusUpdatedAtMs > 0
                    ? FromUnixMs(entry.StatusUpdatedAtMs)
                    : now;
                var created = new CalendarTask
                {
                    Date = entry.Date,
                    Title = ReviewPrefix + normalized,
                    CreatedAt = now
                };
                created.ApplySyncedStatus(entry.Completed, at);
                working.Add(created);
                matchedIds.Add(created.Id);
                creates.Add(new ReviewSyncCreate(created.Id, entry.Date, created.Title, entry.Completed, at));
                continue;
            }

            var canonical = candidates[0];
            matchedIds.Add(canonical.Id);

            foreach (var duplicate in candidates.Skip(1))
            {
                matchedIds.Add(duplicate.Id);
                deletes.Add(new ReviewSyncDelete(duplicate.Id, "同日同名重复任务"));
            }

            if (canonical.IsCompleted == entry.Completed)
            {
                continue;
            }

            // 状态不一致才需要仲裁：谁的状态变更更晚就以谁为准。
            // 时间戳相同（极少见）时以本端为准，保证用户刚点的操作不会被回退。
            var localMs = canonical.StatusTimestamp.ToUnixTimeMilliseconds();
            if (entry.StatusUpdatedAtMs > localMs)
            {
                pulls.Add(new ReviewSyncStatusApply(canonical.Id, entry.Completed, FromUnixMs(entry.StatusUpdatedAtMs)));
            }
            else
            {
                pushes.Add(new ReviewSyncPush(canonical.Id));
            }
        }

        // 对端已经没有的复习任务 = 孤儿：对端删掉了复习周期、或对端本地映射丢失后残留下来的。
        // 清掉它，日历里的复习任务才真正等于对端的复习计划。
        // 保护：计划整体为空时绝不动手——对端可能只是还没把复习计划加载出来，
        //       按空计划清理会把用户的复习任务全部误删。
        if (entries.Count > 0)
        {
            foreach (var task in working)
            {
                if (matchedIds.Contains(task.Id))
                {
                    continue;
                }

                if (expectedKeys.Contains($"{task.Date:yyyy-MM-dd}::{NormalizeTitle(task.Title)}"))
                {
                    continue;
                }

                deletes.Add(new ReviewSyncDelete(task.Id, "对端复习计划中已不存在"));
            }
        }

        return new ReviewSyncPlan(creates, pulls, pushes, deletes);
    }

    private static int PrefixRank(CalendarTask task)
    {
        if (task.Title.StartsWith(ReviewPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return task.Title.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static DateTimeOffset FromUnixMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
}

/// <summary>对端复习计划里的一条复习周期。</summary>
internal sealed record ReviewSyncEntry(DateOnly Date, string Title, bool Completed, long StatusUpdatedAtMs);

/// <summary>一次同步需要执行的全部动作。</summary>
internal sealed record ReviewSyncPlan(
    IReadOnlyList<ReviewSyncCreate> Creates,
    IReadOnlyList<ReviewSyncStatusApply> Pulls,
    IReadOnlyList<ReviewSyncPush> Pushes,
    IReadOnlyList<ReviewSyncDelete> Deletes);

/// <summary>在本地新建一条复习任务（直接带上对端的状态与时间戳）。</summary>
internal sealed record ReviewSyncCreate(Guid Id, DateOnly Date, string Title, bool Completed, DateTimeOffset At);

/// <summary>把对端状态写到本地任务上。</summary>
internal sealed record ReviewSyncStatusApply(Guid TaskId, bool Completed, DateTimeOffset At);

/// <summary>把本地状态推给对端。</summary>
internal sealed record ReviewSyncPush(Guid TaskId);

/// <summary>删除本地复习任务（重复项或孤儿）。</summary>
internal sealed record ReviewSyncDelete(Guid TaskId, string Reason);
