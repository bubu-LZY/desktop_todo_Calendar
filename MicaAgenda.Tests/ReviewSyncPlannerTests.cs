using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Tests;

/// <summary>
/// 复习计划同步的比对/仲裁逻辑（ReviewSyncPlanner）单测。
/// 覆盖：新建、状态一致、两端各自更新（LWW）、时间戳打平、重复任务清理、孤儿清理、空计划保护。
/// </summary>
public sealed class ReviewSyncPlannerTests
{
    private static readonly DateOnly Day = new(2026, 9, 10);
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static CalendarTask MakeTask(
        string title,
        bool completed,
        long updatedAtMs,
        DateOnly? date = null,
        Guid? id = null,
        long? createdAtMs = null)
    {
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs).ToLocalTime();
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs ?? updatedAtMs).ToLocalTime();
        return new CalendarTask
        {
            Id = id ?? Guid.NewGuid(),
            Date = date ?? Day,
            Title = title,
            IsCompleted = completed,
            CompletedAt = completed ? updatedAt : null,
            UpdatedAt = updatedAt,
            CreatedAt = createdAt
        };
    }

    private static ReviewSyncPlan Plan(IReadOnlyList<ReviewSyncEntry> entries, params CalendarTask[] local) =>
        ReviewSyncPlanner.Build(entries, local, Now);

    // ===== 前缀与标题 =====

    [Theory]
    [InlineData("[MM复习]三角函数", true)]
    [InlineData("[复习]三角函数", true)]
    [InlineData("三角函数", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasReviewPrefix_OnlyMatchesReviewTasks(string? title, bool expected) =>
        Assert.Equal(expected, ReviewSyncPlanner.HasReviewPrefix(title));

    [Theory]
    [InlineData("[MM复习]三角函数", "三角函数")]
    [InlineData("[复习]三角函数", "三角函数")]
    [InlineData("  [MM复习]  三角函数  ", "三角函数")]
    [InlineData("三角函数", "三角函数")]
    public void NormalizeTitle_StripsPrefixAndWhitespace(string input, string expected) =>
        Assert.Equal(expected, ReviewSyncPlanner.NormalizeTitle(input));

    [Fact]
    public void BuildTaskTitle_AddsPrefixAndFallsBackWhenEmpty()
    {
        Assert.Equal("[MM复习]三角函数", ReviewSyncPlanner.BuildTaskTitle("三角函数"));
        Assert.Equal("[MM复习]三角函数", ReviewSyncPlanner.BuildTaskTitle("[复习]三角函数"));
        // 对端没给标题时不能生成一个空标题的任务
        Assert.Equal("[MM复习]复习任务", ReviewSyncPlanner.BuildTaskTitle("   "));
    }

    // ===== 新建 / 状态一致 =====

    [Fact]
    public void Build_RemoteEntryMissingLocally_CreatesTaskWithPrefixAndRemoteStatus()
    {
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", true, 1_700_000_000_000L) });

        var created = Assert.Single(plan.Creates);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(Day, created.Date);
        Assert.Equal("[MM复习]三角函数", created.Title);
        Assert.True(created.Completed);
        // 时间戳必须用对端给的时刻，不能用"现在"，否则下一次仲裁会被本地盖过去
        Assert.Equal(1_700_000_000_000L, created.At.ToUnixTimeMilliseconds());
        Assert.Empty(plan.Pulls);
        Assert.Empty(plan.Pushes);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Build_RemoteEntryWithoutTimestamp_StampsNowForCreatedTask()
    {
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", false, 0L) });

        var created = Assert.Single(plan.Creates);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), created.At.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Build_TwoRemoteEntriesWithSameDayAndTitle_CreatesOnlyOneTask()
    {
        // 两个复习周期撞到同一天、或两个节点同名时，日历里只应该有一条任务
        var plan = Plan(new[]
        {
            new ReviewSyncEntry(Day, "三角函数", true, 2_000L),
            new ReviewSyncEntry(Day, "三角函数", true, 2_000L)
        });

        Assert.Single(plan.Creates);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Build_TwoRemoteEntriesSameTask_SecondEntryArbitratesAgainstTheNewTask()
    {
        var plan = Plan(new[]
        {
            new ReviewSyncEntry(Day, "三角函数", true, 2_000L),
            new ReviewSyncEntry(Day, "三角函数", false, 3_000L)
        });

        var created = Assert.Single(plan.Creates);
        // 第二条更晚：应该针对刚才新建的同一条任务回拉状态，而不是再建一条
        var pull = Assert.Single(plan.Pulls);
        Assert.Equal(created.Id, pull.TaskId);
        Assert.False(pull.Completed);
    }

    [Fact]
    public void Build_RemoteTitleEmpty_FallsBackToDefaultAndMatchesExistingTask()
    {
        var tasks = new[] { MakeTask("[MM复习]复习任务", false, 1_000L) };
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "  ", false, 1_000L) }, tasks);

        Assert.Empty(plan.Creates);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Build_SameStatus_ProducesNoActions()
    {
        var tasks = new[] { MakeTask("[MM复习]三角函数", true, 2_000L) };
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", true, 1_000L) }, tasks);

        Assert.Empty(plan.Creates);
        Assert.Empty(plan.Pulls);
        Assert.Empty(plan.Pushes);
        Assert.Empty(plan.Deletes);
    }

    // ===== 时间戳仲裁 =====

    [Fact]
    public void Build_RemoteStatusNewer_PullsRemoteStatus()
    {
        var local = MakeTask("[MM复习]三角函数", false, 1_000L);
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", true, 2_000L) }, local);

        var pull = Assert.Single(plan.Pulls);
        Assert.Equal(local.Id, pull.TaskId);
        Assert.True(pull.Completed);
        Assert.Equal(2_000L, pull.At.ToUnixTimeMilliseconds());
        Assert.Empty(plan.Pushes);
    }

    [Fact]
    public void Build_LocalStatusNewer_PushesLocalStatus()
    {
        var local = MakeTask("[MM复习]三角函数", true, 3_000L);
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", false, 2_000L) }, local);

        var push = Assert.Single(plan.Pushes);
        Assert.Equal(local.Id, push.TaskId);
        Assert.Empty(plan.Pulls);
    }

    [Fact]
    public void Build_TimestampsEqualButStatusDiffers_LocalWins()
    {
        // 时间戳打平（少见）时以本端为准，保证用户刚点的操作不会被对方回退
        var local = MakeTask("[MM复习]三角函数", true, 2_000L);
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", false, 2_000L) }, local);

        Assert.Single(plan.Pushes);
        Assert.Empty(plan.Pulls);
    }

    [Fact]
    public void Build_LegacyPrefixedLocalTask_StillMatchesRemoteEntry()
    {
        // 老版本推送过的任务带 [复习] 前缀，升级后也要能配对，不能被当成孤儿删掉
        var legacy = MakeTask("[复习]三角函数", false, 1_000L);
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", true, 2_000L) }, legacy);

        Assert.Empty(plan.Deletes);
        Assert.Equal(legacy.Id, Assert.Single(plan.Pulls).TaskId);
    }

    // ===== 重复任务清理 =====

    [Fact]
    public void Build_DuplicateTasks_KeepsNewPrefixAndDeletesOthers()
    {
        var legacy = MakeTask("[复习]三角函数", true, 2_000L, createdAtMs: 1_000L);
        var preferred = MakeTask("[MM复习]三角函数", true, 2_000L, createdAtMs: 5_000L);

        // 顺序刻意打乱：正主由"前缀规范 + 创建时间"决定，而不是由集合顺序决定
        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", true, 2_000L) }, legacy, preferred);

        var deleted = Assert.Single(plan.Deletes);
        Assert.Equal(legacy.Id, deleted.TaskId);
        Assert.Empty(plan.Creates);
    }

    [Fact]
    public void Build_ThreeSameDaySameTitle_KeepsOldestNewPrefixedOne()
    {
        var oldest = MakeTask("[MM复习]三角函数", false, 1_000L, createdAtMs: 1_000L);
        var middle = MakeTask("[MM复习]三角函数", false, 1_000L, createdAtMs: 2_000L);
        var newest = MakeTask("[MM复习]三角函数", false, 1_000L, createdAtMs: 3_000L);

        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", false, 1_000L) }, middle, newest, oldest);

        Assert.Equal(2, plan.Deletes.Count);
        Assert.DoesNotContain(plan.Deletes, d => d.TaskId == oldest.Id);
        Assert.Contains(plan.Deletes, d => d.TaskId == middle.Id);
        Assert.Contains(plan.Deletes, d => d.TaskId == newest.Id);
    }

    // ===== 孤儿清理与空计划保护 =====

    [Fact]
    public void Build_LocalTaskMissingFromRemotePlan_IsDeleted()
    {
        var keep = MakeTask("[MM复习]三角函数", false, 1_000L);
        var orphan = MakeTask("[MM复习]世界史", false, 1_000L, date: Day.AddDays(1));

        var plan = Plan(new[] { new ReviewSyncEntry(Day, "三角函数", false, 1_000L) }, keep, orphan);

        var deleted = Assert.Single(plan.Deletes);
        Assert.Equal(orphan.Id, deleted.TaskId);
    }

    [Fact]
    public void Build_EmptyRemotePlan_DeletesNothing()
    {
        // 对端可能只是还没把复习计划加载出来；按空计划清理会把用户的复习任务全部误删
        var tasks = new[] { MakeTask("[MM复习]三角函数", true, 1_000L) };

        var plan = Plan(Array.Empty<ReviewSyncEntry>(), tasks);

        Assert.Empty(plan.Creates);
        Assert.Empty(plan.Pulls);
        Assert.Empty(plan.Pushes);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Build_NonReviewTasksAreNotTouched()
    {
        // 只有调用方过滤后的复习任务会传进来；这里再确认一下用户自建任务不会因为
        // "标题没前缀"而被判定成需要清理的对象（HasReviewPrefix 是准入条件）。
        var userTask = MakeTask("买菜", false, 1_000L);
        Assert.False(ReviewSyncPlanner.HasReviewPrefix(userTask.Title));
    }
}
