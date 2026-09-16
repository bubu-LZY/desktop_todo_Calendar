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
        var task = Call("add_task", """
        {
          "title": "重要会议",
          "date": "2026-09-20",
          "time": "14:30",
          "reminders": ["提前一天", "提前30分钟", "到时提醒"]
        }
        """);

        Assert.Equal("2026-09-20", task.GetProperty("date").GetString());
        Assert.Equal("14:30", task.GetProperty("time").GetString());
        Assert.Equal("2026-09-20T14:30:00", task.GetProperty("scheduledAt").GetString());
        Assert.False(task.GetProperty("isOverdue").GetBoolean());

        // 档位按下拉顺序回传：到时提醒(0) / 提前30分钟(30) / 提前一天(1440)
        Assert.Equal(
            [0, 30, 1440],
            task.GetProperty("reminderLeadMinutes").EnumerateArray().Select(el => el.GetInt32()).ToArray());
        Assert.Equal(
            ["到时提醒", "提前30分钟", "提前一天"],
            task.GetProperty("reminders").EnumerateArray().Select(el => el.GetString()!).ToArray());

        var stored = Assert.Single(_data.Tasks);
        Assert.Equal(new TimeOnly(14, 30), stored.Time);
        Assert.Equal(0, stored.ReminderLeadMinutes);
        Assert.Equal([30, 1440], stored.AdditionalReminderLeadMinutes);
        Assert.Equal(1, _dataChangedCount);
    }

    [Fact]
    public void AddTask_DefaultsToNoTimeAndNoReminder_OmitsAreBackwardCompatible()
    {
        var task = Call("add_task", """{ "title": "普通任务", "date": "2026-09-20" }""");

        Assert.Null(task.GetProperty("time").GetString());
        // 没设时间仍有基准时刻（当天 9:00）
        Assert.Equal("2026-09-20T09:00:00", task.GetProperty("scheduledAt").GetString());
        Assert.Empty(task.GetProperty("reminderLeadMinutes").EnumerateArray());
        Assert.Empty(task.GetProperty("reminders").EnumerateArray());
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
    public void AddTask_RemindersDeduplicateAndSortByCatalogOrder()
    {
        var task = Call("add_task", """
        {
          "title": "t",
          "date": "2026-09-20",
          "reminders": ["提前30分钟", "到时提醒", "提前30分钟"]
        }
        """);

        Assert.Equal(
            [0, 30],
            task.GetProperty("reminderLeadMinutes").EnumerateArray().Select(el => el.GetInt32()).ToArray());
    }

    [Fact]
    public void UpdateTask_ReplacesRemindersAndClearsTime_AndResetsFiredMarks()
    {
        var added = Call("add_task", """
        {
          "title": "开会",
          "date": "2026-09-20",
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
        Assert.Equal([0, 1440], updated.GetProperty("reminderLeadMinutes")
            .EnumerateArray().Select(el => el.GetInt32()).ToArray());
        Assert.False(stored.IsReminderFired(0));
        Assert.False(stored.IsReminderFired(1440));
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
        // 今天深夜的未完成任务：还没到点，不算逾期
        Call("add_task", $$"""{"title":"今晚","date":"{{today:yyyy-MM-dd}}","time":"23:30"}""");
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
}
