using System.Text.Json;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Tests;

/// <summary>
/// MCP 管理类工具测试：周期任务系列的查 / 改 / 删全部、报告发送与预览、立即备份、
/// 清除已完成，以及按 id 批量查询。报告 / 备份能力用假的 <see cref="McpHostActions"/> 注入，
/// 避免测试真的去发网络请求。
/// </summary>
public sealed class McpServerManagementTests
{
    private readonly CalendarData _data = new();
    private readonly object _syncRoot = new();
    private int _dataChangedCount;

    private McpServer Create(McpHostActions? hostActions = null)
        => new(_data, _syncRoot, () => _dataChangedCount++, "test-token", hostActions: hostActions);

    private static JsonElement Invoke(McpServer mcp, string tool, string argumentsJson = "{}")
    {
        var result = mcp.InvokeToolForTest(tool, argumentsJson);
        return JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement;
    }

    [Fact]
    public void ListRecurringSeries_ReturnsRuleAndInstanceCount()
    {
        var mcp = Create();
        Invoke(mcp, "add_recurring_task",
            """{"title":"每周组会","frequency":"weekly","interval":1,"date":"2026-09-18","end":"2026-10-16"}""");

        var list = Invoke(mcp, "list_recurring_series");

        Assert.Equal(1, list.GetProperty("count").GetInt32());
        var series = list.GetProperty("series").EnumerateArray().Single();
        Assert.Equal("每周组会", series.GetProperty("title").GetString());
        Assert.Equal("weekly", series.GetProperty("frequency").GetString());
        Assert.Equal(1, series.GetProperty("interval").GetInt32());
        Assert.Equal("2026-09-18", series.GetProperty("startDate").GetString());
        Assert.Equal("2026-10-16", series.GetProperty("endDate").GetString());
        // 2026-09-18 起每周一次到 10-16：09-25、10-02、10-09、10-16 共 4 个实例
        Assert.Equal(4, series.GetProperty("instanceCount").GetInt32());
        // 普通任务不会被误当成周期系列
        Assert.DoesNotContain(_data.Tasks, t => t.Recurrence == RecurrenceFrequency.None && t.SeriesId is null && t.Title == "每周组会");
    }

    [Fact]
    public void ListRecurringSeries_EmptyWhenNoSeries()
    {
        var mcp = Create();
        Invoke(mcp, "add_task", """{"title":"普通任务","date":"2026-09-18","reminders":[]}""");

        var list = Invoke(mcp, "list_recurring_series");

        Assert.Equal(0, list.GetProperty("count").GetInt32());
    }

    [Fact]
    public void UpdateRecurringSeries_RebuildsInstancesWithNewRule()
    {
        var mcp = Create();
        var created = Invoke(mcp, "add_recurring_task",
            """{"title":"组会","frequency":"weekly","date":"2026-09-18","end":"2026-10-16"}""");
        var seriesId = created.GetProperty("series").GetString()!;

        // 改成「隔周」：旧实例应被删掉，按新规则重建成 10-02、10-16
        var updated = Invoke(mcp, "update_recurring_series",
            $$"""{"id":"{{seriesId}}","title":"双周组会","interval":2}""");

        Assert.Equal(4, updated.GetProperty("removedInstances").GetInt32());
        Assert.Equal(2, updated.GetProperty("createdInstances").GetInt32());

        var series = Invoke(mcp, "list_recurring_series").GetProperty("series").EnumerateArray().Single();
        Assert.Equal("双周组会", series.GetProperty("title").GetString());
        Assert.Equal(2, series.GetProperty("interval").GetInt32());
        Assert.Equal(2, series.GetProperty("instanceCount").GetInt32());
    }

    [Fact]
    public void UpdateRecurringSeries_UnknownId_Throws()
    {
        var mcp = Create();
        var ex = Assert.Throws<KeyNotFoundException>(
            () => mcp.InvokeToolForTest("update_recurring_series", $$"""{"id":"{{Guid.NewGuid()}}","title":"x"}"""));
        Assert.Contains("recurring series not found", ex.Message);
    }

    [Fact]
    public void DeleteAllRecurringSeries_RemovesAllSeriesButKeepsPlainTasks()
    {
        var mcp = Create();
        Invoke(mcp, "add_recurring_task", """{"title":"A","frequency":"daily","date":"2026-09-18","end":"2026-09-20"}""");
        Invoke(mcp, "add_recurring_task", """{"title":"B","frequency":"weekly","date":"2026-09-18","end":"2026-10-09"}""");
        Invoke(mcp, "add_task", """{"title":"普通","date":"2026-09-18","reminders":[]}""");

        var result = Invoke(mcp, "delete_all_recurring_series");

        Assert.Equal(2, result.GetProperty("removedSeries").GetInt32());
        var remaining = Assert.Single(_data.Tasks);
        Assert.Equal("普通", remaining.Title);
    }

    [Fact]
    public void ClearCompletedTasks_RemovesOnlyCompleted()
    {
        var mcp = Create();
        Invoke(mcp, "add_task", """{"title":"要做","date":"2026-09-18","reminders":[]}""");
        var done = Invoke(mcp, "add_task", """{"title":"做完","date":"2026-09-18","reminders":[]}""").GetProperty("id").GetString()!;
        Invoke(mcp, "complete_task", $$"""{"id":"{{done}}"}""");

        var result = Invoke(mcp, "clear_completed_tasks");

        Assert.Equal(1, result.GetProperty("removed").GetInt32());
        var remaining = Assert.Single(_data.Tasks);
        Assert.Equal("要做", remaining.Title);
    }

    [Fact]
    public void SendReport_UsesInjectedHostAction()
    {
        var calls = 0;
        var mcp = Create(new McpHostActions
        {
            SendReportAsync = () =>
            {
                calls++;
                return Task.FromResult<(bool, string)>((true, "已推送到：飞书"));
            }
        });

        var result = Invoke(mcp, "send_report");

        Assert.True(result.GetProperty("sent").GetBoolean());
        Assert.Equal("已推送到：飞书", result.GetProperty("message").GetString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void PreviewReport_ReturnsInjectedText()
    {
        var mcp = Create(new McpHostActions { PreviewReport = () => "本周完成 3 / 5" });

        var result = Invoke(mcp, "preview_report");

        Assert.Equal("本周完成 3 / 5", result.GetProperty("report").GetString());
    }

    [Fact]
    public void RunBackup_ReturnsInjectedMessage()
    {
        var mcp = Create(new McpHostActions { RunBackupAsync = () => Task.FromResult("已保存到本地：C:/b.json") });

        var result = Invoke(mcp, "run_backup");

        Assert.Equal("已保存到本地：C:/b.json", result.GetProperty("message").GetString());
    }

    [Fact]
    public void HostDependentTools_WithoutHostActions_ReportNotReady()
    {
        var mcp = Create();

        // 未注入时必须是明确的「未就绪」，而不是 NRE —— AI 拿到这种错误才知道要让用户去配置
        foreach (var tool in new[] { "send_report", "preview_report", "run_backup" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => mcp.InvokeToolForTest(tool));
            Assert.Contains("未注入", ex.Message);
        }
    }

    [Fact]
    public void QueryTasks_ByIds_ReturnsOnlyMatching()
    {
        var mcp = Create();
        var first = Invoke(mcp, "add_task", """{"title":"甲","date":"2026-09-18","reminders":[]}""").GetProperty("id").GetString()!;
        Invoke(mcp, "add_task", """{"title":"乙","date":"2026-09-19","reminders":[]}""");
        var third = Invoke(mcp, "add_task", """{"title":"丙","date":"2026-09-20","reminders":[]}""").GetProperty("id").GetString()!;

        var result = Invoke(mcp, "query_tasks", $$"""{"ids":["{{first}}","{{third}}"]}""");

        Assert.Equal(2, result.GetProperty("count").GetInt32());
        var titles = result.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("title").GetString()).ToArray();
        Assert.Contains("甲", titles);
        Assert.Contains("丙", titles);
    }

    [Fact]
    public void BatchTasks_AppliesMixedOperationsInOneCall()
    {
        var mcp = Create();
        var seeded = Invoke(mcp, "add_task", """{"title":"旧的","date":"2026-09-18","reminders":[]}""").GetProperty("id").GetString()!;

        var result = Invoke(mcp, "batch_tasks", $$"""
        {
          "operations": [
            { "action": "add", "title": "新甲", "date": "2026-09-19", "time": "14:30", "reminders": ["提前30分钟"] },
            { "action": "add", "title": "新乙", "date": "2026-09-19", "reminders": [] },
            { "action": "update", "id": "{{seeded}}", "title": "改过的" },
            { "action": "complete", "id": "{{seeded}}" }
          ]
        }
        """);

        Assert.Equal(4, result.GetProperty("results").GetArrayLength());
        Assert.Equal(3, _data.Tasks.Count);
        var updated = _data.Tasks.Single(t => t.Id == Guid.Parse(seeded));
        Assert.Equal("改过的", updated.Title);
        Assert.True(updated.IsCompleted);
        Assert.Contains(_data.Tasks, t => t.Title == "新甲" && t.Time == new TimeOnly(14, 30));
    }
}
