using System.Text.Json;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Tests;

/// <summary>
/// MCP 工具层测试：直接走 InvokeToolForTest（不碰 HttpListener / 端口），
/// 覆盖任务时间、多选提醒（含「提前一天」）、查询过滤与参数校验。
/// </summary>
public sealed class McpServerTests
{
    private readonly CalendarData _data = new();
    private readonly McpServer _mcp;
    private int _dataChangedCount;

    public McpServerTests()
    {
        _mcp = new McpServer(_data, new object(), () => _dataChangedCount++, token: "test-token");
    }

    private JsonElement Call(string tool, string argumentsJson)
    {
        var result = _mcp.InvokeToolForTest(tool, argumentsJson);
        var json = JsonSerializer.Serialize(result);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void AddTask_SupportsTimeAndMultipleRemindersIncludingOneDay()
    {
        // 日期用「相对今天」算出来，不要写死。
        // 这个测试原来写死 2026-09-20 14:30 并断言 isOverdue=false —— 到了当天 14:30 之后
        // 任务就成了逾期，断言必挂（2026-09-20 15:58 真的翻过这一次车）。
        var date = DateOnly.FromDateTime(DateTime.Now).AddDays(45);
        var task = Call("add_task", $$"""
        {
          "title": "重要会议",
          "date": "{{date:yyyy-MM-dd}}",
          "time": "14:30",
          "reminders": ["提前一天", "提前30分钟", "到时提醒"]
        }
        """);

        Assert.Equal($"{date:yyyy-MM-dd}", task.GetProperty("date").GetString());
        Assert.Equal("14:30", task.GetProperty("time").GetString());
        Assert.Equal($"{date:yyyy-MM-dd}T14:30:00", task.GetProperty("scheduledAt").GetString());
        Assert.False(task.GetProperty("isOverdue").GetBoolean());

        // 顺序与传入一致：提前一天(1440) / 提前30分钟(30) / 到时提醒(0)
        Assert.Equal(
            [1440, 30, 0],
            task.GetProperty("reminderLeadMinutes").EnumerateArray().Select(el => el.GetInt32()).ToArray());
        Assert.Equal(
            ["提前一天", "提前30分钟", "到时提醒"],
            task.GetProperty("reminders").EnumerateArray().Select(el => el.GetString()!).ToArray());

        var stored = Assert.Single(_data.Tasks);
        Assert.Equal(new TimeOnly(14, 30), stored.Time);
        // 存储布局：第一个为「主提醒」，其余进 Additional
        Assert.Equal(1440, stored.ReminderLeadMinutes);
        Assert.Equal([30, 0], stored.AdditionalReminderLeadMinutes);
        Assert.Equal(1, _dataChangedCount);
    }

    [Fact]
    public void AddTask_DefaultsToNoTimeAndFifteenMinuteReminder()
    {
        var task = Call("add_task", """{ "title": "普通任务", "date": "2026-09-20" }""");

        Assert.Null(task.GetProperty("time").GetString());
        // 没设时间仍有基准时刻（当天 9:00）
        Assert.Equal("2026-09-20T09:00:00", task.GetProperty("scheduledAt").GetString());
        // 新口径：不指定提醒 = 默认「提前 15 分钟」
        Assert.Equal([15], task.GetProperty("reminderLeadMinutes").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(["提前15分钟"], task.GetProperty("reminders").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.True(_data.Tasks.Single().HasReminders);
    }

    [Fact]
    public void AddTask_EmptyReminders_IsExplicitNoReminder()
    {
        var task = Call("add_task", """{ "title": "不提醒", "date": "2026-09-20", "reminders": [] }""");

        // 空数组 = 明确「不提醒」，与「省略」严格区分
        Assert.Empty(task.GetProperty("reminderLeadMinutes").EnumerateArray());
        Assert.False(_data.Tasks.Single().HasReminders);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("9点")]
    [InlineData("abc")]
    public void AddTask_InvalidTime_ThrowsWithGuidance(string badTime)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _mcp.InvokeToolForTest("add_task", $$"""{"title":"t","time":"{{badTime}}"}"""));
        Assert.Contains("HH:mm", ex.Message);
    }

    [Fact]
    public void AddTask_UnknownReminderLabel_ThrowsAndListsValidLabels()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _mcp.InvokeToolForTest("add_task", """{"title":"t","reminders":["提前两小时"]}"""));
        Assert.Contains("提前两小时", ex.Message);
        Assert.Contains("提前一天", ex.Message);
    }

    [Fact]
    public void AddTask_RemindersDeduplicateAndPreserveCallerOrder()
    {
        var task = Call("add_task", """
        {
          "title": "t",
          "date": "2026-09-20",
          "reminders": ["提前30分钟", "到时提醒", "提前30分钟"]
        }
        """);

        // 去重 + 保留传入顺序（30 在 0 之前），不再按档位表排序
        Assert.Equal(
            [30, 0],
            task.GetProperty("reminderLeadMinutes").EnumerateArray().Select(el => el.GetInt32()).ToArray());
    }

    [Fact]
    public void UpdateTask_ReplacesRemindersAndClearsTime_AndResetsFiredMarks()
    {
        // 用「明确落在过去」的日期，避免断言依赖运行当天的时刻/时区：
        // 只要任务日已经是昨天，下面 1440 档位的触发点与 60 分钟补发宽限都必定已过。
        var past = DateOnly.FromDateTime(DateTime.Now).AddDays(-3);
        var added = Call("add_task", $$"""
        {
          "title": "开会",
          "date": "{{past:yyyy-MM-dd}}",
          "time": "14:30",
          "reminders": ["提前30分钟"]
        }
        """);
        var id = added.GetProperty("id").GetString()!;
        var stored = _data.Tasks.Single();

        // 主提醒（30）已推过：改时间后这个标记必须作废，否则新时间不会再提醒
        stored.MarkReminderSent(30, DateTimeOffset.Now);
        Assert.True(stored.IsReminderFired(30));

        var updated = Call("update_task", $$"""
        {
          "id": "{{id}}",
          "time": "16:00",
          "reminders": ["提前一天", "到时提醒"]
        }
        """);

        Assert.Equal("16:00", updated.GetProperty("time").GetString());
        // 顺序与传入一致（提前一天 → 到时提醒），不再被档位表重排
        Assert.Equal([1440, 0], updated.GetProperty("reminderLeadMinutes")
            .EnumerateArray().Select(el => el.GetInt32()).ToArray());

        // 改时间后旧的「30 已推」标记必须作废（30 已不在档位里，读它天然为 false）
        Assert.False(stored.IsReminderFired(30));
        // 1440 是新的主提醒档位，且补发宽限早已过去 → 直接记为已推
        // —— 这是刻意的防「改完立刻蹦陈旧提醒」行为，不是漏推。
        Assert.True(stored.IsReminderFired(1440));
        // 「到时提醒」(0) 同样早已过点，也应记为已推（当天补发窗口也过了）
        Assert.True(stored.IsReminderFired(0));
    }

    [Fact]
    public void UpdateTask_EmptyRemindersArrayClearsAllReminders()
    {
        var added = Call("add_task", """
        {"title":"t","date":"2026-09-20","reminders":["提前30分钟","到时提醒"]}
        """);
        var id = added.GetProperty("id").GetString()!;

        var updated = Call("update_task", $$"""{"id":"{{id}}","reminders":[]}""");

        Assert.Empty(updated.GetProperty("reminders").EnumerateArray());
        Assert.False(_data.Tasks.Single().HasReminders);
    }

    [Fact]
    public void UpdateTask_ChangingDateResetsFiredMarks()
    {
        var added = Call("add_task", """
        {"title":"t","date":"2026-09-20","reminders":["到时提醒"]}
        """);
        var id = added.GetProperty("id").GetString()!;
        var stored = _data.Tasks.Single();
        stored.MarkReminderSent(0, DateTimeOffset.Now);

        _mcp.InvokeToolForTest("update_task", $$"""{"id":"{{id}}","date":"2026-09-25"}""");

        Assert.False(stored.IsReminderFired(0));
        Assert.Equal(new DateOnly(2026, 9, 25), stored.Date);
    }

    [Fact]
    public void QueryTasks_FiltersByStatusAndOverdueUsesTaskTime()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        // 昨天的未完成任务：逾期
        Call("add_task", $$"""{"title":"欠账","date":"{{today.AddDays(-1):yyyy-MM-dd}}"}""");
        // 未来的未完成任务（用「现在 + 2 小时」的日期/时刻，任何时刻运行都不会逾期；
        // 旧版硬编码 "23:30"，一旦测试跑在 23:30 之后，"今晚"也逾期，Assert.Single 必挂）。
        var later = now.AddHours(2);
        Call("add_task", $$"""{"title":"稍后","date":"{{DateOnly.FromDateTime(later):yyyy-MM-dd}}","time":"{{TimeOnly.FromDateTime(later):HH:mm}}"}""");
        // 今天已完成
        var done = Call("add_task", $$"""{"title":"搞定","date":"{{today:yyyy-MM-dd}}"}""");
        Call("complete_task", $$"""{"id":"{{done.GetProperty("id").GetString()}}"}""");

        var open = Call("query_tasks", """{"range":"all","status":"open"}""");
        Assert.Equal(2, open.GetProperty("count").GetInt32());

        var completed = Call("query_tasks", """{"range":"all","status":"completed"}""");
        Assert.Single(completed.GetProperty("tasks").EnumerateArray());

        var overdue = Call("query_tasks", """{"range":"all","status":"overdue"}""");
        var overdueTask = Assert.Single(overdue.GetProperty("tasks").EnumerateArray());
        Assert.Equal("欠账", overdueTask.GetProperty("title").GetString());
        Assert.True(overdueTask.GetProperty("isOverdue").GetBoolean());
    }

    [Fact]
    public void QueryTasks_FiltersByStartEndRangeAndKeyword()
    {
        Call("add_task", """{"title":"周一站会","date":"2026-09-14"}""");
        Call("add_task", """{"title":"周三评审","date":"2026-09-16"}""");
        Call("add_task", """{"title":"周五团建","date":"2026-09-18"}""");

        var range = Call("query_tasks", """{"start":"2026-09-15","end":"2026-09-17"}""");
        var rangeTitles = range.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("title").GetString()!).ToArray();
        Assert.Equal(["周三评审"], rangeTitles);

        // start 单侧开口：9/17 起只有 9/18 的团建
        var from = Call("query_tasks", """{"start":"2026-09-17"}""");
        Assert.Equal(1, from.GetProperty("count").GetInt32());

        var search = Call("query_tasks", """{"range":"all","q":"评审"}""");
        Assert.Single(search.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public void QueryTasks_InvalidStatusAndRange_Throw()
    {
        Assert.Throws<ArgumentException>(() => _mcp.InvokeToolForTest("query_tasks", """{"status":"bogus"}"""));
        Assert.Throws<ArgumentException>(() => _mcp.InvokeToolForTest("query_tasks", """{"range":"fortnight"}"""));
        Assert.Throws<ArgumentException>(() =>
            _mcp.InvokeToolForTest("query_tasks", """{"start":"2026-09-20","end":"2026-09-01"}"""));
    }

    [Fact]
    public void BatchTasks_AddWithTimeAndReminders_UpdateSharesSameValidation()
    {
        var result = Call("batch_tasks", """
        {
          "operations": [
            {"action":"add","title":"批量任务A","date":"2026-09-20","time":"08:15","reminders":["提前一天"]},
            {"action":"add","title":"批量任务B","date":"2026-09-20","time":"坏时间"}
          ]
        }
        """);

        var results = result.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal("08:15", results[0].GetProperty("time").GetString());
        Assert.Equal([1440], results[0].GetProperty("reminderLeadMinutes")
            .EnumerateArray().Select(el => el.GetInt32()).ToArray());

        // 第二条格式错误只让自己失败，不影响第一条已落库
        Assert.Contains("HH:mm", results[1].GetProperty("error").GetString());
        Assert.Single(_data.Tasks);
    }

    [Fact]
    public void QueryTasks_SortsByDateThenScheduledTime()
    {
        Call("add_task", """{"title":"晚","date":"2026-09-20","time":"18:00"}""");
        Call("add_task", """{"title":"早","date":"2026-09-20","time":"08:00"}""");
        Call("add_task", """{"title":"默认九点","date":"2026-09-20"}""");

        var tasks = Call("query_tasks", """{"date":"2026-09-20"}""");
        Assert.Equal(
            ["早", "默认九点", "晚"],
            tasks.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("title").GetString()!).ToArray());
    }

    // ===== 本次新增：写后校验 / 提醒来源标注 / 日期口径 =====

    [Fact]
    public void AddTask_ReturnsOkAndVerifiedSoCallerCanTellRealSuccess()
    {
        var task = Call("add_task", """{"title":"真写入了","date":"2026-09-20"}""");

        // 历史坑：工具报 success 但任务并不存在，调用方后续 delete 才报 task not found。
        // 现在以 verified 为判据，且 id 与集合里的实体必须一致。
        Assert.True(task.GetProperty("ok").GetBoolean());
        Assert.True(task.GetProperty("verified").GetBoolean());
        Assert.Equal(_data.Tasks.Single().Id, task.GetProperty("id").GetGuid());
    }

    [Fact]
    public void AddTask_WithoutReminders_MarksSourceAsDefault()
    {
        var task = Call("add_task", """{"title":"没传提醒","date":"2026-09-20"}""");

        // 省略 reminders 会被服务端补上默认「提前15分钟」——必须让调用方看得出来，
        // 否则"我本意不提醒"会被静默改写。
        Assert.Equal("default", task.GetProperty("reminderSource").GetString());
        Assert.Equal([15], task.GetProperty("reminderLeadMinutes")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public void AddTask_WithExplicitReminders_MarksSourceAsExplicit()
    {
        var empty = Call("add_task", """{"title":"明确不提醒","date":"2026-09-20","reminders":[]}""");
        Assert.Equal("explicit", empty.GetProperty("reminderSource").GetString());
        Assert.Empty(empty.GetProperty("reminderLeadMinutes").EnumerateArray());

        var given = Call("add_task", """{"title":"指定档位","date":"2026-09-20","reminders":["提前一天"]}""");
        Assert.Equal("explicit", given.GetProperty("reminderSource").GetString());
    }

    [Fact]
    public void UpdateTask_ReturnsVerified()
    {
        var added = Call("add_task", """{"title":"改我","date":"2026-09-20"}""");
        var id = added.GetProperty("id").GetString()!;

        var updated = Call("update_task", $$"""{"id":"{{id}}","title":"改过了"}""");

        Assert.True(updated.GetProperty("verified").GetBoolean());
        Assert.Equal("改过了", updated.GetProperty("title").GetString());
    }

    [Fact]
    public void Reminders_ReturnedInCallerSuppliedOrder_NotCatalogOrder()
    {
        // 传入「一天 → 30分钟」（临近时刻排序），返回必须保持这个顺序。
        // 历史坑：服务端按档位表重排成「提前30分钟, 提前一天」，调用方以为数据被改写。
        var task = Call("add_task", """
        {
          "title": "顺序",
          "date": "2026-09-20",
          "reminders": ["提前一天", "提前30分钟"]
        }
        """);

        Assert.Equal(
            ["提前一天", "提前30分钟"],
            task.GetProperty("reminders").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void QueryTasks_WeekIsMondayToSunday_AndEchoesResolvedWindow()
    {
        // 构造一个一定落在"本周"里的日期，避免测试依赖运行当天的星期。
        var today = DateOnly.FromDateTime(DateTime.Now);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        Call("add_task", $$"""{"title":"周一","date":"{{monday:yyyy-MM-dd}}"}""");
        Call("add_task", $$"""{"title":"周日","date":"{{monday.AddDays(6):yyyy-MM-dd}}"}""");
        // 边界外：上周日 / 下周一 都不该被 range=week 捞到
        Call("add_task", $$"""{"title":"上周日","date":"{{monday.AddDays(-1):yyyy-MM-dd}}"}""");
        Call("add_task", $$"""{"title":"下周一","date":"{{monday.AddDays(7):yyyy-MM-dd}}"}""");

        var week = Call("query_tasks", """{"range":"week"}""");

        // 自然周口径：周一为界，周日仍属本周
        Assert.Equal(monday.ToString("yyyy-MM-dd"), week.GetProperty("resolvedStart").GetString());
        Assert.Equal(monday.AddDays(6).ToString("yyyy-MM-dd"), week.GetProperty("resolvedEnd").GetString());
        Assert.Equal("monday", week.GetProperty("weekStartsOn").GetString());

        var titles = week.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("title").GetString()!).ToArray();
        // 顺序无关：中文字符串的 OrderBy 在不同 ICU/culture 下结果不同（CI 与本地会不一致），
        // 这里只关心「命中了哪两条」，不关心排列。
        Assert.Equal(2, titles.Length);
        Assert.Contains("周一", titles);
        Assert.Contains("周日", titles);
    }

    [Fact]
    public void QueryTasks_MonthAndYearAlsoEchoResolvedWindow()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var month = Call("query_tasks", """{"range":"month"}""");
        Assert.Equal(new DateOnly(today.Year, today.Month, 1).ToString("yyyy-MM-dd"),
            month.GetProperty("resolvedStart").GetString());
        Assert.Equal(new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).ToString("yyyy-MM-dd"),
            month.GetProperty("resolvedEnd").GetString());

        // range=all 不设边界，两侧都应是 null
        var all = Call("query_tasks", """{"range":"all"}""");
        Assert.Equal(JsonValueKind.Null, all.GetProperty("resolvedStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, all.GetProperty("resolvedEnd").ValueKind);
    }

    [Fact]
    public void QueryTasks_ExplicitStartEnd_EchoesWhatServerActuallyUsed()
    {
        var range = Call("query_tasks", """{"start":"2026-09-15","end":"2026-09-17"}""");

        Assert.Equal("2026-09-15", range.GetProperty("resolvedStart").GetString());
        Assert.Equal("2026-09-17", range.GetProperty("resolvedEnd").GetString());
        // 原样回显调用方传入的单边开口，与 resolved* 区分开
        var from = Call("query_tasks", """{"start":"2026-09-17"}""");
        Assert.Equal("2026-09-17", from.GetProperty("start").GetString());
        Assert.Equal(JsonValueKind.Null, from.GetProperty("end").ValueKind);
    }

    // ===== compute_date：相对日期换算（Issue 1 的根治手段）=====

    [Fact]
    public void ComputeDate_NextTuesday_FromSaturday_IsTheFollowingTuesdayNotTheOneAfter()
    {
        // 复刻真实翻车现场：今天 2026-09-19（周六）说「下周二」。
        // 正确 = 9/22；当时被心算成 9/29。自然周口径下，9/19 所在周是 9/14~9/20，
        // 下周是 9/21~9/27，故「下周二」= 9/22。
        var result = Call("compute_date", """{"baseline":"2026-09-19","weekday":"tuesday","weekAnchor":"next"}""");

        Assert.Equal("2026-09-22", result.GetProperty("date").GetString());
        Assert.Equal("周二", result.GetProperty("weekday").GetString());
    }

    [Fact]
    public void ComputeDate_ThisWeekday_StaysInsideCurrentNaturalWeek()
    {
        // 2026-09-19 是周六；「本周三」= 9/16（已过也仍返回本周那天）
        var result = Call("compute_date", """{"baseline":"2026-09-19","weekday":"wednesday","weekAnchor":"this"}""");

        Assert.Equal("2026-09-16", result.GetProperty("date").GetString());
    }

    [Fact]
    public void ComputeDate_NextWeekday_FromSunday_UsesComingWeekNotTheOneAfter()
    {
        // 2026-09-20 是周日，属 9/14~9/20 这一周；「下周一」应是 9/21（次日起算那周），不是 9/28
        var result = Call("compute_date", """{"baseline":"2026-09-20","weekday":"monday","weekAnchor":"next"}""");

        Assert.Equal("2026-09-21", result.GetProperty("date").GetString());
    }

    [Theory]
    [InlineData(3, "2026-09-22")]
    [InlineData(-3, "2026-09-16")]
    [InlineData(0, "2026-09-19")]
    public void ComputeDate_OffsetDays(int offset, string expected)
    {
        var result = Call("compute_date", $$"""{"baseline":"2026-09-19","offsetDays":{{offset}}}""");
        Assert.Equal(expected, result.GetProperty("date").GetString());
    }

    [Fact]
    public void ComputeDate_OffsetMonthsAndWeeks()
    {
        var month = Call("compute_date", """{"baseline":"2026-09-19","offsetMonths":1}""");
        Assert.Equal("2026-10-19", month.GetProperty("date").GetString());

        var week = Call("compute_date", """{"baseline":"2026-09-19","offsetWeeks":2}""");
        Assert.Equal("2026-10-03", week.GetProperty("date").GetString());
    }

    [Fact]
    public void ComputeDate_SupportsChineseWeekdayNames_AndExplains()
    {
        var result = Call("compute_date", """{"baseline":"2026-09-19","weekday":"周五","weekAnchor":"next"}""");

        Assert.Equal("2026-09-25", result.GetProperty("date").GetString());
        // 算法过程要能核对，不能只给个数字让人猜
        Assert.Contains("2026-09-19", result.GetProperty("explanation").GetString());
        Assert.Contains("2026-09-25", result.GetProperty("explanation").GetString());
    }

    [Fact]
    public void ComputeDate_UnknownWeekday_ThrowsWithGuidance()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _mcp.InvokeToolForTest("compute_date", """{"weekday":"礼拜八"}"""));
        Assert.Contains("unknown weekday", ex.Message);
    }
}
