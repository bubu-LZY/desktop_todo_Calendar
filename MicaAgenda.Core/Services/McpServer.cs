using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>MCP 工具的元数据：名称、说明、输入 JSON Schema。MCP tools/list 与 AI function calling 共用。</summary>
/// <remarks>
/// MCP 协议要求工具对象使用小驼峰键 <c>name</c>/<c>description</c>/<c>inputSchema</c>，
/// 这里显式标注 JSON 名，避免 record 默认按 PascalCase 序列化导致外部客户端解析失败。
/// </remarks>
public sealed record ToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] object InputSchema);

/// <summary>
/// 变更类工具（add / update / complete 等）的统一返回外壳：任务字段平铺在外层，
/// 另外附带三个只读的元字段，让调用方能一眼确认「这次到底写没写进去」。
/// </summary>
/// <param name="Task">任务本身的字段（原 ToDto 的匿名对象）。</param>
/// <param name="TaskId">本次操作的任务 id，直接用它可以做后续 update / delete。</param>
/// <param name="ReminderSource">
/// 提醒档位的来源：<c>default</c> = 调用方没传 reminders，服务端按默认「提前15分钟」写的；
/// <c>explicit</c> = 调用方显式指定（含空数组 = 不提醒）。只有 add 类会带这个字段。
/// </param>
internal sealed class WriteResult(object task, Guid taskId, string? reminderSource)
{
    [JsonPropertyName("ok")]
    public bool Ok => true;

    /// <summary>写后回读校验通过。调用方应以它为"真成功"的判据，而不是只看工具有没有报错。</summary>
    [JsonPropertyName("verified")]
    public bool Verified => true;

    [JsonPropertyName("verifiedAt")]
    public DateTimeOffset VerifiedAt { get; } = DateTimeOffset.Now;

    [JsonPropertyName("id")]
    public Guid TaskId { get; } = taskId;

    [JsonPropertyName("reminderSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReminderSource { get; } = reminderSource;

    // 任务字段平铺在外层：保持与旧返回体兼容（调用方照旧读 task.date / task.title）
    [JsonExtensionData]
    public IDictionary<string, object?> Fields { get; } = ToDictionary(task);

    private static Dictionary<string, object?> ToDictionary(object dto)
    {
        var json = JsonSerializer.SerializeToElement(dto);
        var map = new Dictionary<string, object?>();
        foreach (var prop in json.EnumerateObject())
        {
            map[prop.Name] = prop.Value.Clone();
        }

        return map;
    }
}

/// <summary>
/// 内置 MCP Server（Model Context Protocol），采用 Streamable HTTP transport。
/// 外部 AI 客户端（Claude Desktop / Cursor 等）可通过
/// http://localhost:&lt;port&gt;/mcp 连接，调用任务增删改查等工具。
/// </summary>
public sealed partial class McpServer : IDisposable
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "micaagenda";

    /// <summary>
    /// 握手时上报的版本号：直接取程序集版本（单一来源 Directory.Build.props），
    /// 不再手写常量——否则每次发版都要记得回来改，容易和真实版本漂移。
    /// </summary>
    private static string ServerVersion => UpdateService.Normalize(UpdateService.CurrentAppVersion());

    private readonly CalendarData _data;
    private readonly object _syncRoot;
    private readonly Action _onDataChanged;
    private readonly HttpListener _listener = new();
    private readonly string _token;
    // 复习任务被删除时通知宿主（同步删掉对端复习周期），可选：老调用方不需要双向删除
    private readonly Action<CalendarTask>? _onReviewTaskDeleted;
    // 复习任务完成态变化时通知宿主（立刻回推对端），与上面那条对称
    private readonly Action<CalendarTask>? _onReviewTaskStatusChanged;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private readonly Func<AppConfig>? _configProvider;
    private readonly McpHostActions? _hostActions;
    private AiAgentService? _aiAgent;

    /// <param name="onReviewTaskStatusChanged">
    /// 完成态被改动的复习任务回调：宿主据此立刻推给 my-mindmap agent，
    /// 与 <paramref name="onReviewTaskDeleted"/> 对称。
    /// </param>
    public McpServer(
        CalendarData data,
        object syncRoot,
        Action onDataChanged,
        string token,
        Action<CalendarTask>? onReviewTaskDeleted = null,
        Action<CalendarTask>? onReviewTaskStatusChanged = null,
        Func<AppConfig>? configProvider = null,
        McpHostActions? hostActions = null)
    {
        _onReviewTaskDeleted = onReviewTaskDeleted;
        _onReviewTaskStatusChanged = onReviewTaskStatusChanged;
        _data = data;
        _syncRoot = syncRoot;
        _onDataChanged = onDataChanged;
        _token = token ?? string.Empty;
        _configProvider = configProvider;
        _hostActions = hostActions;
    }

    public int Port { get; private set; } = 17802;

    public IReadOnlyList<string> ActivePrefixes => _listener.Prefixes.Cast<string>().ToArray();

    /// <summary>启动服务，返回是否成功。</summary>
    public bool Start(int port)
    {
        Port = port;
        var candidates = new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" };
        foreach (var prefix in candidates)
        {
            try
            {
                _listener.Prefixes.Add(prefix);
            }
            catch
            {
                // 忽略无法注册的前缀
            }
        }

        if (_listener.Prefixes.Count == 0)
        {
            return false;
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            return false;
        }

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunLoopAsync(_cts.Token));
        // 观察后台循环任务，避免遗漏异常变成"未观察异常"被 finalizer 反复打印
        _ = _runTask.ContinueWith(
            t => AppLog.Error(t.Exception?.Flatten().InnerException, "McpServer.RunLoopTask"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    public void Stop()
    {
        // Stop 与 Close 分别兜底：若 Stop 抛异常也要保证 Close 执行，否则监听句柄会泄漏
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略取消异常
        }

        try
        {
            _listener.Stop();
        }
        catch
        {
            // 忽略停止异常
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                // 监听已停止
                break;
            }
            catch (ObjectDisposedException)
            {
                // Dispose 已释放 listener，等待中的调用必然失败，正常退出即可
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "McpServer.RunLoop");
                break;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "/";
            if (path != "/mcp" && path != string.Empty)
            {
                context.Response.StatusCode = 404;
                return;
            }

            await HandleMcpAsync(context);
        }
        catch (Exception ex)
        {
            // 回写错误响应本身也可能失败（客户端已断开），必须再次兜底，
            // 否则异常会逃逸成未观察任务异常。
            try
            {
                await WriteJsonRpcErrorAsync(context.Response, null, -32603, ex.Message);
            }
            catch
            {
                // 无法回写错误响应，忽略
            }
        }
        finally
        {
            // finally 中的异常不会被上面的 catch 捕获，必须就地消化
            try
            {
                context.Response.Close();
            }
            catch
            {
                // 忽略响应关闭失败
            }
        }
    }

    private async Task HandleMcpAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // CORS 严格收敛到本机 origin：MCP 设计上只服务本地客户端，
        // 设成 "*" 反而让任意网页都能跨源调用，无端扩大攻击面。
        // 没有 Origin 头的（如 curl / 本机进程直连）直接放行。
        var origin = request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin)
            && !IsLocalOrigin(origin)
            && !string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 403;
            return;
        }

        if (!string.IsNullOrEmpty(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Vary"] = "Origin";
        }
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Mcp-Session-Id, Authorization, X-Auth-Token";
        response.Headers["Access-Control-Allow-Methods"] = "POST, GET, DELETE, OPTIONS";

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            return;
        }

        // Token 鉴权：empty token 时**直接拒绝**，不再保留"无 token 裸奔"的旁路。
        // 否则只要 MCP 单独开启、API 关闭（这次会跳过 Token 生成），
        // 整个端点就完全无鉴权，攻击者只要能访问 127.0.0.1:17802 就能改任务。
        if (string.IsNullOrEmpty(_token) || !CheckAuth(request))
        {
            response.StatusCode = 401;
            await WriteJsonRpcErrorAsync(response, null, -32001, "unauthorized: missing or invalid token");
            return;
        }

        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            return;
        }

        // MCP body 上限 1 MB：MCP 消息都是结构化 JSON，没必要无上限读
        if (request.ContentLength64 > 1024 * 1024)
        {
            response.StatusCode = 413;
            await WriteJsonRpcErrorAsync(response, null, -32001, "payload too large");
            return;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            response.StatusCode = 400;
            return;
        }

        // 没声明 Content-Length 时的兜底
        if (Encoding.UTF8.GetByteCount(body) > 1024 * 1024)
        {
            response.StatusCode = 413;
            await WriteJsonRpcErrorAsync(response, null, -32001, "payload too large");
            return;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;
        var hasId = root.TryGetProperty("id", out var idEl);

        if (method is null)
        {
            response.StatusCode = 400;
            return;
        }

        // JSON-RPC notification（无 id）：处理但不返回结果
        if (!hasId)
        {
            if (method == "notifications/initialized")
            {
                response.StatusCode = 202;
                return;
            }

            response.StatusCode = 202;
            return;
        }

        var id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText();
        if (id is null)
        {
            id = idEl.GetRawText();
        }

        switch (method)
        {
            case "initialize":
                response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
                await WriteJsonRpcResultAsync(response, id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = ServerName, version = ServerVersion }
                });
                return;

            case "ping":
                await WriteJsonRpcResultAsync(response, id, new { });
                return;

            case "tools/list":
                await WriteJsonRpcResultAsync(response, id, new { tools = GetToolDefinitions() });
                return;

            case "tools/call":
                await HandleToolCallAsync(response, id, root);
                return;

            default:
                await WriteJsonRpcErrorAsync(response, id, -32601, $"method not found: {method}");
                return;
        }
    }

    private async Task HandleToolCallAsync(HttpListenerResponse response, string id, JsonElement root)
    {
        var hasParams = root.TryGetProperty("params", out var paramsEl)
                        && paramsEl.ValueKind == JsonValueKind.Object;
        var name = hasParams && paramsEl.TryGetProperty("name", out var nameEl)
            ? nameEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(name))
        {
            await WriteJsonRpcErrorAsync(response, id, -32602, "missing tool name");
            return;
        }

        var arguments = hasParams && paramsEl.TryGetProperty("arguments", out var argsEl)
            ? argsEl
            : default;

        object result;
        var isError = false;
        try
        {
            result = InvokeTool(name, arguments);
        }
        catch (Exception ex)
        {
            result = new { error = ex.Message };
            isError = true;
        }

        var text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        await WriteJsonRpcResultAsync(response, id, new
        {
            content = new[] { new { type = "text", text } },
            isError
        });
    }

    private object InvokeTool(string name, JsonElement args)
    {
        return name switch
        {
            "query_tasks" => QueryTasks(
                GetArgString(args, "range"),
                GetArgDate(args, "date"),
                GetArgDate(args, "start"),
                GetArgDate(args, "end"),
                GetArgString(args, "status"),
                GetArgString(args, "q"),
                GetArgGuidList(args, "ids")),
            "compute_date" => ComputeDate(args),
            "add_task" => AddTask(
                GetArgDate(args, "date"),
                GetArgString(args, "title"),
                GetArgBool(args, "isImportant"),
                GetArgTime(args, "time"),
                GetArgReminderLeads(args, "reminders")),
            "update_task" => UpdateTask(GetArgGuid(args, "id"), args),
            "delete_task" => DeleteTask(GetArgGuid(args, "id")),
            "add_recurring_task" => AddRecurringTask(
                GetArgDate(args, "date"),
                GetArgString(args, "title"),
                GetArgFrequency(args, "frequency"),
                GetArgInt(args, "interval") ?? 1,
                GetArgDate(args, "end"),
                GetArgTime(args, "time"),
                GetArgReminderLeads(args, "reminders")),
            "delete_recurring_series" => DeleteRecurringSeries(GetArgGuid(args, "id")),
            "list_recurring_series" => ListRecurringSeries(),
            "update_recurring_series" => UpdateRecurringSeries(GetArgGuid(args, "id"), args),
            "delete_all_recurring_series" => DeleteAllRecurringSeries(),
            "send_report" => SendReport(),
            "preview_report" => PreviewReport(),
            "run_backup" => RunBackup(),
            "clear_completed_tasks" => ClearCompletedTasks(),
            "list_ai_models" => ListAiModelsAsync().GetAwaiter().GetResult(),
            "test_ai_connection" => TestAiConnectionAsync().GetAwaiter().GetResult(),
            "ai_chat" => ChatWithAiAsync(GetArgString(args, "message")).GetAwaiter().GetResult(),
            "complete_task" => SetCompletion(GetArgGuid(args, "id"), true),
            "uncomplete_task" => SetCompletion(GetArgGuid(args, "id"), false),
            "batch_tasks" => BatchTasks(args),
            _ => throw new ArgumentException($"unknown tool: {name}")
        };
    }

    /// <summary>
    /// 相对日期换算器：把「下周X / N 天后 / 下个月X号 / 本月底」之类的说法，
    /// 用<strong>服务端自己的日历</strong>算成绝对日期，而不是让调用方（或模型）心算。
    ///
    /// 这是 Issue「下周二 == 9/29 还是 9/22」的根治办法：模型心算日期是不可靠的，
    /// 但只要它把 baseline（通常传 today）+ 规则交给这里，结果就由代码保证正确。
    /// 因此这个工具本身不做任何"聪明"的猜测 —— 语义全部由显式参数决定。
    /// </summary>
    private object ComputeDate(JsonElement args)
    {
        // 基准日：默认今天；调用方也可以指定（例如"从 9/15 那周算下周X"）
        var baseline = GetArgDate(args, "baseline") ?? DateOnly.FromDateTime(DateTime.Now);

        var offsetDays = GetArgInt(args, "offsetDays");
        var offsetWeeks = GetArgInt(args, "offsetWeeks");
        var offsetMonths = GetArgInt(args, "offsetMonths");
        var weekday = GetArgString(args, "weekday");
        var weekAnchor = (GetArgString(args, "weekAnchor") ?? "this").Trim().ToLowerInvariant();

        var reason = new List<string>();
        var date = baseline;

        if (offsetDays is { } d)
        {
            date = date.AddDays(d);
            reason.Add($"基准日 {d:+#;-#;0} 天");
        }

        if (offsetWeeks is { } w)
        {
            date = date.AddDays(w * 7);
            reason.Add($"基准日 {w:+#;-#;0} 周");
        }

        if (offsetMonths is { } m)
        {
            date = date.AddMonths(m);
            reason.Add($"基准日 {m:+#;-#;0} 个月");
        }

        if (!string.IsNullOrWhiteSpace(weekday))
        {
            var target = ParseWeekday(weekday);
            // 「本周X」与「下周X」都按自然周（周一为起点）算：
            //   本周X → 本周内的周X（若今天已过该天，仍返回本周那天，调用方需自行决定要不要 +7）
            //   下周X → 下周内的周X
            var weekStart = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));   // 该周周一
            var shift = weekAnchor switch
            {
                "next" => 7,
                "last" => -7,
                _ => 0
            };
            date = weekStart.AddDays(shift + (((int)target + 6) % 7));       // 周一=0 … 周日=6
            reason.Add($"{(weekAnchor switch { "next" => "下", "last" => "上", _ => "本" })}周{WeekdayLabelOf(target)}");
        }

        return new
        {
            date = date.ToString("yyyy-MM-dd"),
            weekday = WeekdayLabelOf(date.DayOfWeek),
            baseline = baseline.ToString("yyyy-MM-dd"),
            today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"),
            // 把算法过程写出来，调用方（和读日志的人）能核对，不用猜
            explanation = reason.Count == 0
                ? "未提供任何偏移参数，返回基准日本身"
                : $"{baseline:yyyy-MM-dd} 起，{string.Join(" → ", reason)} = {date:yyyy-MM-dd}"
        };
    }

    /// <summary>解析星期说法：支持 monday/mon/周一/星期一/1 等写法。</summary>
    private static DayOfWeek ParseWeekday(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t switch
        {
            "monday" or "mon" or "周一" or "星期一" or "1" => DayOfWeek.Monday,
            "tuesday" or "tue" or "周二" or "星期二" or "2" => DayOfWeek.Tuesday,
            "wednesday" or "wed" or "周三" or "星期三" or "3" => DayOfWeek.Wednesday,
            "thursday" or "thu" or "周四" or "星期四" or "4" => DayOfWeek.Thursday,
            "friday" or "fri" or "周五" or "星期五" or "5" => DayOfWeek.Friday,
            "saturday" or "sat" or "周六" or "星期六" or "6" => DayOfWeek.Saturday,
            "sunday" or "sun" or "周日" or "周天" or "星期日" or "星期天" or "7" or "0" => DayOfWeek.Sunday,
            _ => throw new ArgumentException(
                $"unknown weekday: \"{text}\"; valid: monday..sunday / 周一..周日 / 1..7")
        };
    }

    /// <summary>星期几的中文短名（"周一" … "周日"）。</summary>
    private static string WeekdayLabelOf(DayOfWeek day)
        => MicaAgenda.App.Helpers.WeekdayText.Of(day);

    /// <summary>
    /// 测试入口：MCP 的工具层不依赖 HTTP 监听，直接喂 JSON 字符串就能调，
    /// 避免单测去绑定端口（HttpListener 在非 Windows / 无 urlacl 环境下不稳定）。
    /// </summary>
    internal object InvokeToolForTest(string name, string? argumentsJson = null)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        return InvokeTool(name, doc.RootElement.Clone());
    }

    private object QueryTasks(string? range, DateOnly? exactDate, DateOnly? start, DateOnly? end, string? status, string? keyword, IReadOnlyList<Guid>? ids = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var nowLocal = DateTime.Now;

        // status 白名单：写错了直接报错并给出合法值，AI 看到错误能自我修正；
        // 静默忽略只会让它拿到"莫名其妙被过滤/没过滤"的结果。
        status = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
        if (status is not ("all" or "open" or "completed" or "overdue"))
        {
            throw new ArgumentException("status must be one of: all, open, completed, overdue");
        }

        if (start is { } s && end is { } e && s > e)
        {
            throw new ArgumentException("start date must not be later than end date");
        }

        var q = keyword?.Trim();

        // 先把「要查哪段日期」解析成一个明确的闭区间，再统一过滤。
        // 这样返回体里能原样回显服务端实际算出来的范围 —— 调用方不必再靠猜
        // （历史坑：range=week 曾经返回空，调用方无从判断是"真没任务"还是"窗口算错了"）。
        var normalizedRange = (range ?? "today").Trim().ToLowerInvariant();
        DateOnly? resolvedStart;
        DateOnly? resolvedEnd;

        if (ids is { Count: > 0 })
        {
            // 按一批 id 批量查询：与 range/date 互斥（优先按 id 取），不限定日期区间
            resolvedStart = null;
            resolvedEnd = null;
        }
        else if (exactDate is not null)
        {
            resolvedStart = exactDate;
            resolvedEnd = exactDate;
        }
        else if (start is not null || end is not null)
        {
            // 只给一边时另一边开口：start = 从那天起；end = 截止那天（含）
            resolvedStart = start;
            resolvedEnd = end;
        }
        else
        {
            (resolvedStart, resolvedEnd) = normalizedRange switch
            {
                "today" => (today, today),
                // 自然周口径：周一 → 周日（与中文习惯一致）。
                // 注意日历「周视图」的格子仍按周日→周六铺（CalendarService），
                // 两者是不同用途：这里是查询窗口，那边是界面网格，刻意不强行统一。
                "week" => (GetNaturalWeekStart(today), GetNaturalWeekStart(today).AddDays(6)),
                "month" => (new DateOnly(today.Year, today.Month, 1),
                            new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month))),
                "year" => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
                "all" => ((DateOnly?)null, (DateOnly?)null),
                var other => throw new ArgumentException(
                    $"unknown range: {other}; valid values: today, week, month, year, all")
            };
        }

        List<CalendarTask> tasks;
        lock (_syncRoot)
        {
            IEnumerable<CalendarTask> source = _data.Tasks;

            if (ids is { Count: > 0 })
            {
                var wanted = ids.ToHashSet();
                source = source.Where(t => wanted.Contains(t.Id));
            }
            else if (resolvedStart is not null || resolvedEnd is not null)
            {
                source = source.Where(t =>
                    (resolvedStart is null || t.Date >= resolvedStart.Value)
                    && (resolvedEnd is null || t.Date <= resolvedEnd.Value));
            }

            tasks = status switch
            {
                "open" => source.Where(t => !t.IsCompleted).ToList(),
                "completed" => source.Where(t => t.IsCompleted).ToList(),
                // 逾期口径与右侧任务面板一致：未完成且已过任务时刻（没设时间按当天 9:00）
                "overdue" => source.Where(t => t.IsOverdueAt(nowLocal)).ToList(),
                _ => source.ToList()
            };

            if (!string.IsNullOrEmpty(q))
            {
                tasks = tasks.Where(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        return new
        {
            range = normalizedRange,
            date = exactDate?.ToString("yyyy-MM-dd"),
            // 原样回显调用方传入的值，与下面 resolved* 区分：传入可能是 null / 单边开口
            start = start?.ToString("yyyy-MM-dd"),
            end = end?.ToString("yyyy-MM-dd"),
            // 服务端实际生效的查询窗口（含端点）。null = 该侧不设边界。
            // 拿到空结果时先看这里对不对，再怀疑数据本身。
            resolvedStart = resolvedStart?.ToString("yyyy-MM-dd"),
            resolvedEnd = resolvedEnd?.ToString("yyyy-MM-dd"),
            today = today.ToString("yyyy-MM-dd"),
            weekStartsOn = ToWeekStartName(today),
            status,
            query = string.IsNullOrEmpty(q) ? null : q,
            count = tasks.Count,
            // 同一天内按任务时刻（没设时间按 9:00）排，而不是随机的列表顺序
            tasks = tasks.OrderBy(t => t.Date).ThenBy(t => t.ScheduledAt).Select(ToDto)
        };
    }

    /// <summary>
    /// 统一在返回体里回显「哪天算一周的开始」，避免调用方自己猜服务端口径。
    /// 今天恰好是周日时，自然周的起点要落到上周一，所以这里不能直接返回今天的星期名。
    /// </summary>
    private static string ToWeekStartName(DateOnly date)
    {
        var start = GetNaturalWeekStart(date);
        return start.DayOfWeek switch
        {
            DayOfWeek.Monday => "monday",
            DayOfWeek.Tuesday => "tuesday",
            DayOfWeek.Wednesday => "wednesday",
            DayOfWeek.Thursday => "thursday",
            DayOfWeek.Friday => "friday",
            DayOfWeek.Saturday => "saturday",
            _ => "sunday"
        };
    }

    private object AddTask(DateOnly? date, string? title, bool? isImportant, TimeOnly? time, IReadOnlyList<int>? reminderLeads)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("title is required");
        }

        var task = new CalendarTask
        {
            Date = date ?? DateOnly.FromDateTime(DateTime.Now),
            Title = title.Trim(),
            IsImportant = isImportant ?? false,
            // 与界面编辑同一口径：显式 09:00 也按"没选时间"落 null，避免同一种语义存成两种形态
            Time = NormalizeTaskTime(time),
            CreatedAt = DateTimeOffset.Now
        };

        // reminders 的三种语义（与 UpdateTask 的「整组替换」保持一致）：
        //   省略 / null → 默认「提前15分钟」，并在返回体里标 reminderSource=default
        //   []          → 明确不提醒，标 reminderSource=explicit
        //   非空数组    → 用传入档位，标 reminderSource=explicit
        var remindersExplicit = reminderLeads is not null;
        task.SetReminderLeads(reminderLeads ?? [ReminderLeadCatalog.DefaultLeadMinutes]);
        // 新建时已超出补发窗口的档位（如给今天的任务勾「提前一天」）直接记为已推，不发陈旧提醒
        task.SuppressMissedLeadReminders(DateTimeOffset.Now);

        lock (_syncRoot)
        {
            _data.Tasks.Add(task);
        }

        _onDataChanged();
        return WithWriteVerification(ToDto(task), task.Id, remindersExplicit ? "explicit" : "default");
    }

    /// <summary>
    /// 变更类工具统一收尾：用主键回读一次，确认数据真的落进了集合。
    ///
    /// 历史坑：工具返回 success 但任务并不存在（并发覆盖 / 调用方把上一次的返回记到了
    /// 这一次头上 / 客户端缓存了响应），调用方后续按这个 id 去 update/delete 才报
    /// "task not found"，排查成本很高。所以把「写后校验」做成返回体的一部分，
    /// 调用方看到 verified=true 才算真成功 —— 而不是只看工具有没有报错。
    /// </summary>
    private object WithWriteVerification(object dto, Guid taskId, string? reminderSource = null)
    {
        bool verified;
        lock (_syncRoot)
        {
            verified = _data.Tasks.Any(t => t.Id == taskId);
        }

        if (!verified)
        {
            // 真出现"写完却读不到"是严重信号：宁可让调用方拿到错误，也不能报 success
            throw new InvalidOperationException(
                $"write verification failed: task {taskId} not found right after write");
        }

        return new WriteResult(dto, taskId, reminderSource);
    }

    private object UpdateTask(Guid id, JsonElement args)
    {
        lock (_syncRoot)
        {
            var task = _data.Tasks.FirstOrDefault(t => t.Id == id)
                ?? throw new KeyNotFoundException($"task not found: {id}");

            if (TryGetArg(args, "title", out var titleEl) && !string.IsNullOrWhiteSpace(titleEl.GetString()))
            {
                task.Title = titleEl.GetString()!.Trim();
            }

            var scheduleChanged = false;
            if (TryGetArg(args, "date", out var dateEl) && DateOnly.TryParse(dateEl.GetString(), out var d) && task.Date != d)
            {
                task.Date = d;
                scheduleChanged = true;
            }

            // time：显式给 null / 空串 = 清除时刻（回到当天 9:00 语义）；给了就必须是 HH:mm
            if (TryGetArg(args, "time", out var timeEl))
            {
                var newTime = NormalizeTaskTime(ParseTimeElement(timeEl));
                if (task.Time != newTime)
                {
                    task.Time = newTime;
                    scheduleChanged = true;
                }
            }

            // reminders：只要带了这个键就是「整组替换」；[] 或 null = 清空全部提醒
            if (TryGetArg(args, "reminders", out var reminderEl))
            {
                var newLeads = ParseReminderLeadsElement(reminderEl);
                if (!task.AllReminderLeads.SequenceEqual(newLeads))
                {
                    task.SetReminderLeads(newLeads);
                    scheduleChanged = true;
                }
            }

            if (TryGetArg(args, "isImportant", out var impEl) && impEl.ValueKind == JsonValueKind.True)
            {
                task.IsImportant = true;
            }
            else if (TryGetArg(args, "isImportant", out impEl) && impEl.ValueKind == JsonValueKind.False)
            {
                task.IsImportant = false;
            }

            // 日期 / 时刻 / 提醒档位任一变化，都要把各档位的「已推」标记作废，
            // 否则旧的到点标记会让新时刻的提醒永远推不出来；
            // 同时把新计划里已经过了补发窗口的档位直接记账，避免改完立刻蹦陈旧提醒。
            if (scheduleChanged)
            {
                task.ResetReminder();
                task.SuppressMissedLeadReminders(DateTimeOffset.Now);
            }
        }

        _onDataChanged();
        lock (_syncRoot)
        {
            return WithWriteVerification(ToDto(_data.Tasks.First(t => t.Id == id)), id);
        }
    }

    private object DeleteTask(Guid id)
    {
        CalendarTask removed;
        lock (_syncRoot)
        {
            removed = _data.Tasks.FirstOrDefault(t => t.Id == id)
                ?? throw new KeyNotFoundException($"task not found: {id}");
            _data.Tasks.Remove(removed);
        }

        // 锁外通知宿主：复习任务要同步删掉对端的复习周期，否则会被下一次同步建回来
        if (removed.IsReviewTask)
        {
            _onReviewTaskDeleted?.Invoke(removed);
        }

        _onDataChanged();
        return new { id, deleted = true };
    }

    /// <summary>创建周期任务：源任务 + 物化未来实例（封顶 2 年或到 end）。</summary>
    private object AddRecurringTask(
        DateOnly? date, string? title, RecurrenceFrequency frequency, int interval,
        DateOnly? end, TimeOnly? time, IReadOnlyList<int>? reminderLeads)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("title is required");
        }

        var master = new CalendarTask
        {
            Date = date ?? DateOnly.FromDateTime(DateTime.Now),
            Title = title.Trim(),
            Time = NormalizeTaskTime(time),
            Recurrence = frequency,
            RecurrenceInterval = Math.Max(1, interval),
            RecurrenceEnd = end,
            CreatedAt = DateTimeOffset.Now
        };
        master.SetReminderLeads(reminderLeads ?? [ReminderLeadCatalog.DefaultLeadMinutes]);
        master.SuppressMissedLeadReminders(DateTimeOffset.Now);

        var instances = RecurrenceService.Expand(master);
        foreach (var instance in instances)
        {
            instance.SuppressMissedLeadReminders(DateTimeOffset.Now);
        }

        lock (_syncRoot)
        {
            _data.Tasks.Add(master);
            _data.Tasks.AddRange(instances);
        }

        _onDataChanged();

        // 周期任务一次写多条：校验"源任务在 + 实例数对得上"才算真成功
        int actualInstances;
        bool masterExists;
        lock (_syncRoot)
        {
            masterExists = _data.Tasks.Any(t => t.Id == master.Id);
            actualInstances = _data.Tasks.Count(t => t.SeriesId == master.Id);
        }

        if (!masterExists || actualInstances != instances.Count)
        {
            throw new InvalidOperationException(
                $"write verification failed: recurring series {master.Id} " +
                $"masterExists={masterExists}, expected {instances.Count} instances but found {actualInstances}");
        }

        return new
        {
            ok = true,
            verified = true,
            id = master.Id,
            created = actualInstances + 1,
            series = master.Id
        };
    }

    /// <summary>删除整个周期任务系列（源任务 + 全部物化实例）。</summary>
    private object DeleteRecurringSeries(Guid id)
    {
        List<CalendarTask> removed;
        lock (_syncRoot)
        {
            removed = _data.Tasks.Where(t => t.Id == id || t.SeriesId == id).ToList();
            if (removed.Count == 0)
            {
                throw new KeyNotFoundException($"recurring series not found: {id}");
            }

            foreach (var task in removed)
            {
                _data.Tasks.Remove(task);
            }
        }

        // 与 DeleteTask 同口径：删到复习任务时通知对端一起删（周期任务标题带复习前缀时可能触发）。
        foreach (var task in removed)
        {
            if (task.IsReviewTask)
            {
                _onReviewTaskDeleted?.Invoke(task);
            }
        }

        _onDataChanged();
        return new { id, deleted = removed.Count };
    }

    // ===== AI 助手工具（需宿主注入 configProvider）=====

    private async Task<object> ListAiModelsAsync()
    {
        var config = RequireAiConfig();
        using var client = new OpenAiClient();
        var models = await client.ListModelsAsync(config.AiBaseUrl, config.AiApiKey);
        return new { models };
    }

    private async Task<object> TestAiConnectionAsync()
    {
        var config = RequireAiConfig();
        using var client = new OpenAiClient();
        var message = await client.TestConnectionAsync(config.AiBaseUrl, config.AiApiKey, config.AiModel);
        return new { ok = message.StartsWith("连接成功", StringComparison.Ordinal), message };
    }

    /// <summary>通过 AI 对话执行任务操作：模型经 function calling 调用上面的任务工具（增删改查）。</summary>
    private async Task<object> ChatWithAiAsync(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("message is required");
        }

        _ = RequireAiConfig();
        _aiAgent ??= new AiAgentService(_data, _syncRoot, () => _configProvider!(), _onDataChanged, _hostActions);
        var reply = await _aiAgent.ChatAsync([new AiChatMessage("user", message)]);
        return new { reply };
    }

    private AppConfig RequireAiConfig()
        => _configProvider?.Invoke()
           ?? throw new InvalidOperationException("AI 未配置：宿主未注入 configProvider。");

    private object SetCompletion(Guid id, bool completed)
    {
        CalendarTask? changedReviewTask = null;
        lock (_syncRoot)
        {
            var task = _data.Tasks.FirstOrDefault(t => t.Id == id)
                ?? throw new KeyNotFoundException($"task not found: {id}");

            var wasCompleted = task.IsCompleted;
            if (completed)
            {
                task.MarkCompleted(DateTimeOffset.Now);
            }
            else
            {
                task.MarkIncomplete();
            }

            if (task.IsCompleted != wasCompleted && task.IsReviewTask)
            {
                changedReviewTask = task;
            }
        }

        _onDataChanged();

        // 锁外通知宿主回推对端（回调内部会走网络）
        if (changedReviewTask is not null)
        {
            _onReviewTaskStatusChanged?.Invoke(changedReviewTask);
        }
        lock (_syncRoot)
        {
            return WithWriteVerification(ToDto(_data.Tasks.First(t => t.Id == id)), id);
        }
    }

    private object BatchTasks(JsonElement args)
    {
        if (!TryGetArg(args, "operations", out var opsEl) || opsEl.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("operations is required");
        }

        var results = new List<object>();
        foreach (var op in opsEl.EnumerateArray())
        {
            var action = op.TryGetProperty("action", out var a) ? a.GetString() : null;
            var opId = op.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var gid) ? gid : (Guid?)null;
            var opDate = op.TryGetProperty("date", out var dateEl) && DateOnly.TryParse(dateEl.GetString(), out var d) ? d : (DateOnly?)null;
            var opTitle = op.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            var opImportant = op.TryGetProperty("isImportant", out var iEl) && iEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? iEl.GetBoolean() : (bool?)null;

            try
            {
                // time / reminders 的校验也放在 try 内：某一条操作的格式错误只让这一条失败，
                // 不能让它中断整批（其余操作已经按"各自独立"的契约执行）。
                var opTime = op.TryGetProperty("time", out var timeEl) ? ParseTimeElement(timeEl) : null;
                var opReminders = op.TryGetProperty("reminders", out var remEl) ? ParseReminderLeadsElement(remEl) : null;

                // update 直接走 UpdateTask(Guid, JsonElement)，与单工具调用同一套口径
                // （白名单参数、改时刻作废已推标记），避免两条实现慢慢跑偏。
                results.Add(action?.ToLowerInvariant() switch
                {
                    "add" or "create" => AddTask(opDate, opTitle, opImportant, opTime, opReminders),
                    "update" or "edit" => UpdateTask(opId ?? Guid.Empty, op),
                    "delete" or "remove" => DeleteTask(opId ?? Guid.Empty),
                    "complete" => SetCompletion(opId ?? Guid.Empty, true),
                    "uncomplete" or "incomplete" => SetCompletion(opId ?? Guid.Empty, false),
                    _ => new { error = $"unknown action: {action}" }
                });
            }
            catch (Exception ex)
            {
                results.Add(new { error = ex.Message });
            }
        }

        _onDataChanged();
        return new { results };
    }

    /// <summary>
    /// 自然周的起点：周一。
    /// .NET 的 <see cref="DayOfWeek"/> 是 0=周日 … 6=周六，所以周日要特殊处理回退 6 天，
    /// 不能直接用 <c>AddDays(-(int)DayOfWeek)</c>（那样周日会得到自己，变成"周日起"）。
    /// </summary>
    private static DateOnly GetNaturalWeekStart(DateOnly date)
        => date.DayOfWeek == DayOfWeek.Sunday
            ? date.AddDays(-6)
            : date.AddDays(-((int)date.DayOfWeek - (int)DayOfWeek.Monday));

    private static object ToDto(CalendarTask task)
    {
        var reminderLeads = task.AllReminderLeads;
        return new
        {
            id = task.Id,
            date = task.Date.ToString("yyyy-MM-dd"),
            // 没设时间为 null（语义上按当天 9:00 处理）；scheduledAt 是真正参与提醒/逾期计算的本地时刻
            time = task.Time?.ToString("HH:mm", CultureInfo.InvariantCulture),
            scheduledAt = task.ScheduledAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            title = task.Title,
            isCompleted = task.IsCompleted,
            isImportant = task.IsImportant,
            // 未完成且已过任务时刻；与任务面板「未完成」组里挂【已逾期】红标的口径一致
            isOverdue = task.IsOverdueAt(DateTime.Now),
            // 多选提醒：分钟数（机器友好）+ 档位标签（人/AI 友好，可原样回传给 add/update）
            reminderLeadMinutes = reminderLeads,
            reminders = ReminderLeadCatalog.ToLabels(reminderLeads),
            createdAt = task.CreatedAt,
            completedAt = task.CompletedAt,
            // 状态最后变更时间（完成和取消完成都会刷新），供对端做时间戳仲裁
            updatedAt = task.UpdatedAt
        };
    }

    private static bool TryGetArg(JsonElement args, string key, out JsonElement value)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetArgString(JsonElement args, string key)
        => TryGetArg(args, key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static DateOnly? GetArgDate(JsonElement args, string key)
        => TryGetArg(args, key, out var el) && el.ValueKind == JsonValueKind.String && DateOnly.TryParse(el.GetString(), out var d) ? d : null;

    private static bool? GetArgBool(JsonElement args, string key)
        => TryGetArg(args, key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : null;

    private static Guid GetArgGuid(JsonElement args, string key)
        => TryGetArg(args, key, out var el) && el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g) ? g : Guid.Empty;

    /// <summary>解析任务 id 数组（query_tasks 的 ids）：缺省 / 非法元素被忽略，空结果返回 null。</summary>
    private static IReadOnlyList<Guid>? GetArgGuidList(JsonElement args, string key)
    {
        if (!TryGetArg(args, key, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<Guid>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id))
            {
                list.Add(id);
            }
        }

        return list.Count == 0 ? null : list;
    }

    private static int? GetArgInt(JsonElement args, string key)
        => TryGetArg(args, key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) ? n : null;

    /// <summary>解析重复频率：daily/weekly/monthly/yearly；非法值直接报错（AI 看到错误能自我修正）。</summary>
    private static RecurrenceFrequency GetArgFrequency(JsonElement args, string key)
    {
        if (!TryGetArg(args, key, out var el) || el.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("frequency is required; must be one of: daily, weekly, monthly, yearly");
        }

        var text = el.GetString()?.Trim().ToLowerInvariant();
        return text switch
        {
            "daily" => RecurrenceFrequency.Daily,
            "weekly" => RecurrenceFrequency.Weekly,
            "monthly" => RecurrenceFrequency.Monthly,
            "yearly" => RecurrenceFrequency.Yearly,
            _ => throw new ArgumentException(
                $"unknown frequency: \"{text}\"; valid: daily, weekly, monthly, yearly")
        };
    }

    /// <summary>落盘前的时刻归一化：没选、或正好 09:00 都记 null（与界面 / ViewModel 同一口径）。</summary>
    private static TimeOnly? NormalizeTaskTime(TimeOnly? time)
        => time is null || time == CalendarTask.DefaultTime ? null : time;

    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss"];

    /// <summary>add_task 的 time 参数：缺省 / null / 空串 = 不设时刻（按当天 9:00）；格式错直接抛异常。</summary>
    private static TimeOnly? GetArgTime(JsonElement args, string key)
        => !TryGetArg(args, key, out var el) ? null : ParseTimeElement(el);

    /// <summary>
    /// 解析时刻元素。JSON null / 空串 = 清除（null）；
    /// 只接受 HH:mm（也兼容 H:mm、HH:mm:ss），拒绝 "9点"、"25:00" 这类值，避免静默吞掉 AI 的笔误。
    /// </summary>
    private static TimeOnly? ParseTimeElement(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("time must be a string in HH:mm format (e.g. \"14:30\")");
        }

        var text = el.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return time;
        }

        throw new ArgumentException($"invalid time: \"{text}\"; expected HH:mm (e.g. \"14:30\")");
    }

    /// <summary>
    /// add_task / add_recurring_task 的 reminders 参数：
    /// 缺省 / JSON null = 未指定（走默认「提前15分钟」）；空数组 = 明确「不提醒」；
    /// 档位标签数组（可多选，如 ["提前30分钟","提前一天"]）。
    /// </summary>
    private static IReadOnlyList<int>? GetArgReminderLeads(JsonElement args, string key)
    {
        if (!TryGetArg(args, key, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ParseReminderLeadsElement(el);
    }

    /// <summary>
    /// 把提醒档位标签数组换算成分钟列表。
    /// JSON null / 空数组 = 不提醒；元素必须是字符串且落在固定档位表内，
    /// 无法识别的标签直接报错并附上合法值——AI 拼错档位名时能照错误信息改正。
    ///
    /// 顺序<b>保持调用方传入的顺序</b>（只去重）：这样返回体里的 reminders 与调用方给的
    /// 完全一致，不会出现「传进去 [提前一天, 提前30分钟] 却回成 [提前30分钟, 提前一天]」
    /// 这种让人以为数据被改写的情况。
    /// </summary>
    private static IReadOnlyList<int> ParseReminderLeadsElement(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (el.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                $"reminders must be an array of labels, e.g. [\"提前30分钟\",\"提前一天\"]; valid: {string.Join(", ", ReminderLeadCatalog.SelectableLabels)}");
        }

        var leads = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("each reminder must be a string label");
            }

            var label = item.GetString();
            if (string.IsNullOrWhiteSpace(label) || ReminderLeadCatalog.ToMinutes(label) is not { } minutes)
            {
                throw new ArgumentException(
                    $"unknown reminder label: \"{label}\"; valid: {string.Join(", ", ReminderLeadCatalog.SelectableLabels)}");
            }

            if (!leads.Contains(minutes))
            {
                leads.Add(minutes);
            }
        }

        return leads;
    }

    /// <summary>提醒档位的固定枚举，schema 与 <see cref="ReminderLeadCatalog"/> 共用一份，避免两处漂移。</summary>
    private static string[] ReminderEnum => ReminderLeadCatalog.SelectableLabels.ToArray();

    internal static List<ToolDefinition> GetToolDefinitions()
    {
        return new List<ToolDefinition>
        {
            Tool("query_tasks", "查询任务清单。range 可选 today(今日,默认)/week(自然周:周一→周日)/month(自然月)/year(自然年)/all(全部)；date 指定某一天(YYYY-MM-DD)；也可用 start/end 查任意日期区间(含端点)。status 可选 all(默认)/open(未完成,含逾期)/completed(已完成)/overdue(仅逾期)。q 可选,按标题关键词模糊匹配。date 与 start/end 都给时以 date 为准。返回体里 resolvedStart/resolvedEnd 是服务端实际生效的窗口,weekStartsOn 回显周起点(恒为 monday) —— 拿到空结果时先核对这两个字段,再怀疑数据本身。**推荐始终显式传 start/end**,range 只是便捷写法。",
                new { type = "object", properties = new
                {
                    range = new { type = "string", description = "查询范围(无 date、start、end 时生效)。week 为自然周:周一→周日", @enum = new[] { "today", "week", "month", "year", "all" } },
                    date = new { type = "string", description = "指定某一天 YYYY-MM-DD(服务端不做相对日期推算,请调用方先算好绝对日期)" },
                    start = new { type = "string", description = "区间起始日期 YYYY-MM-DD(含)。与 end 搭配使用最不容易出歧义" },
                    end = new { type = "string", description = "区间结束日期 YYYY-MM-DD(含)" },
                    status = new { type = "string", description = "完成状态过滤", @enum = new[] { "all", "open", "completed", "overdue" } },
                    q = new { type = "string", description = "标题关键词(不区分大小写,包含匹配)" },
                    ids = new
                    {
                        type = "array",
                        description = "按一批任务 id 批量查询(如从上一次结果里挑几条再查详情)；传入时忽略 range/date,status 仍生效",
                        items = new { type = "string" }
                    }
                } }),
            Tool("compute_date", "把相对日期说法换算成绝对日期。**所有涉及相对时间的操作都建议先调它**，不要心算 —— 实测「下周二」这类说法心算极易算成再下一周。参数可组合：offsetDays/offsetWeeks/offsetMonths 为相对基准日的偏移；weekday + weekAnchor 定位到某一周的星期几。返回 date(YYYY-MM-DD)、weekday 和 explanation(算法过程,便于核对)。基线默认取服务端今天。",
                new { type = "object", properties = new
                {
                    baseline = new { type = "string", description = "基准日 YYYY-MM-DD(默认今天)。例如『从 9/15 那周算下周X』就传 2026-09-15" },
                    offsetDays = new { type = "integer", description = "相对基准日加减的天数(可为负)。『三天后』= 3" },
                    offsetWeeks = new { type = "integer", description = "相对基准日加减的周数(可为负)" },
                    offsetMonths = new { type = "integer", description = "相对基准日加减的月数(可为负)。『下个月同一天』= 1" },
                    weekday = new { type = "string", description = "目标星期几:monday..sunday 或 周一..周日" },
                    weekAnchor = new { type = "string", description = "weekday 的相对周:this(本周,默认)/next(下周)/last(上周)", @enum = new[] { "this", "next", "last" } }
                } }),
            Tool("add_task", "在某一天添加任务。date 省略默认今天；time 为任务时刻 HH:mm(省略或 null 表示不设具体时间,按当天 9:00 处理)；reminders 为提醒档位标签数组,可多选。**省略 reminders 会默认带「提前15分钟」**(返回体里 reminderSource=default 会标明这一点);要明确不提醒必须显式传空数组 []。可选值到时提醒/提前3分钟/提前5分钟/提前10分钟/提前15分钟/提前30分钟/提前1个小时/提前3个小时/提前一天。date 必须是绝对日期 YYYY-MM-DD —— 不管是「下周二」「三天后」「这个月15号」「国庆前那个周五」还是直接给日期,都由调用方先换算成绝对日期再传,服务端不做任何相对时间推算;算不准时用 compute_date 工具,不要心算。返回体含 ok/verified/id,verified=true 才表示真的写入成功。",
                new { type = "object", properties = new
                {
                    title = new { type = "string", description = "任务标题" },
                    date = new { type = "string", description = "绝对日期 YYYY-MM-DD(如 2026-09-22)。相对时间请先用 compute_date 换算" },
                    time = new { type = "string", description = "任务时刻 HH:mm,如 14:30；null 表示不设具体时间" },
                    isImportant = new { type = "boolean", description = "是否重要" },
                    reminders = new
                    {
                        type = "array",
                        description = "提醒档位标签,可多选;每个档位各提醒一次,如 [\"提前一天\",\"提前30分钟\"]。省略 = 默认「提前15分钟」；[] = 明确不提醒",
                        items = new { type = "string", @enum = ReminderEnum }
                    }
                }, required = new[] { "title" } }),
            Tool("update_task", "编辑任务。id 必填,title/date/time/isImportant/reminders 均可选,只改传入的字段。time 传 null 或空串清除时刻；reminders 只要传入就整组替换,传 [] 清空全部提醒。修改日期/时刻/提醒后,旧的已提醒记录会作废,按新计划重新提醒。date 必须是绝对日期。返回体含 ok/verified,verified=true 才表示真的改成功 —— 历史上有过「工具报成功但任务并不存在」的情况,请以 verified 为准。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "任务 id" },
                    title = new { type = "string", description = "新标题" },
                    date = new { type = "string", description = "新日期 YYYY-MM-DD(绝对日期)" },
                    time = new { type = "string", description = "新时刻 HH:mm；null/空串 = 清除时刻" },
                    isImportant = new { type = "boolean", description = "是否重要" },
                    reminders = new
                    {
                        type = "array",
                        description = "整组替换提醒档位；[] = 不提醒。回传顺序与传入顺序一致",
                        items = new { type = "string", @enum = ReminderEnum }
                    }
                }, required = new[] { "id" } }),
            Tool("delete_task", "删除任务。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "任务 id" }
                }, required = new[] { "id" } }),
            Tool("add_recurring_task", "创建周期(重复)任务：生成源任务及其未来重复实例(封顶约 2 年或到 end 日期)。frequency 必填(daily/weekly/monthly/yearly)；interval 为间隔(每 N 个频率单位，默认 1)；end 为结束日期(可选，含当天)；time/reminders 与 add_task 同(省略 reminders = 默认「提前15分钟」；[] = 不提醒)。date/end 均为绝对日期。返回体含 ok/verified/id,verified=true 且 created 与实例数一致才算真成功。",
                new { type = "object", properties = new
                {
                    title = new { type = "string", description = "任务标题" },
                    frequency = new { type = "string", description = "重复频率", @enum = new[] { "daily", "weekly", "monthly", "yearly" } },
                    interval = new { type = "integer", description = "间隔(每 N 天/周/月/年一次，默认 1)" },
                    date = new { type = "string", description = "起始日期 YYYY-MM-DD(绝对日期,默认今天)" },
                    end = new { type = "string", description = "结束日期 YYYY-MM-DD(可选,含当天)" },
                    time = new { type = "string", description = "任务时刻 HH:mm" },
                    reminders = new
                    {
                        type = "array",
                        description = "提醒档位标签,可多选。省略 = 默认「提前15分钟」；[] = 不提醒",
                        items = new { type = "string", @enum = ReminderEnum }
                    }
                }, required = new[] { "title", "frequency" } }),
            Tool("delete_recurring_series", "删除整个周期任务系列(源任务 + 全部重复实例)。id 为源任务 id(series id)。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "源任务 id(series id)" }
                }, required = new[] { "id" } }),
            Tool("list_recurring_series", "列出所有周期任务系列(源任务)。返回每条系列的 seriesId、标题、规则、起始日期、结束日期、时刻、提醒与已物化实例数。要修改 / 删除某个系列前，先用它拿到 id。",
                new { type = "object", properties = new { } }),
            Tool("update_recurring_series", "修改一个周期任务系列。id 为源任务 id(series id)；title/frequency/interval/end/time/reminders 均可选，只改传入的字段。规则改变后旧实例会被删除并按新规则重建。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "源任务 id(series id)" },
                    title = new { type = "string", description = "新标题" },
                    frequency = new { type = "string", description = "新频率", @enum = new[] { "daily", "weekly", "monthly", "yearly" } },
                    interval = new { type = "integer", description = "新间隔(每 N 个频率单位一次)" },
                    end = new { type = "string", description = "新结束日期 YYYY-MM-DD；null/空串 = 取消结束限制" },
                    time = new { type = "string", description = "新时刻 HH:mm；null/空串 = 清除时刻" },
                    reminders = new
                    {
                        type = "array",
                        description = "整组替换提醒档位；[] = 不提醒",
                        items = new { type = "string", @enum = ReminderEnum }
                    }
                }, required = new[] { "id" } }),
            Tool("delete_all_recurring_series", "删除所有周期任务系列(各自的源任务 + 全部实例)。不可撤销。",
                new { type = "object", properties = new { } }),
            Tool("send_report", "立即把「任务完成情况」报告推送到已配置的渠道(飞书卡片 / 企业微信 markdown / 自定义 webhook)。发送周期(周报 / 月报)与推送渠道由设置决定。",
                new { type = "object", properties = new { } }),
            Tool("preview_report", "预览当前周期的任务完成情况报告文本(不发送)。",
                new { type = "object", properties = new { } }),
            Tool("run_backup", "立即执行一次备份：导出今年任务为 JSON 保存到本地，并按设置推送到飞书 / 企业微信。",
                new { type = "object", properties = new { } }),
            Tool("clear_completed_tasks", "删除所有已完成任务。返回删除条数。",
                new { type = "object", properties = new { } }),
            Tool("complete_task", "标记任务为已完成。返回体含 ok/verified,verified=true 才表示真的改成功。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "任务 id" }
                }, required = new[] { "id" } }),
            Tool("uncomplete_task", "取消任务的完成状态。返回体含 ok/verified。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "任务 id" }
                }, required = new[] { "id" } }),
            Tool("batch_tasks", "批量增删改任务：operations 为操作数组，按顺序逐项执行，单条失败不影响其余（逐条返回结果）。每项 action 必填：add/create(新增,需 title)、update/edit(修改,需 id)、delete/remove(删除,需 id)、complete(标记完成,需 id)、uncomplete(取消完成,需 id)。可带 title/date/time/isImportant/reminders；reminders 只要传入就整组替换，[] = 不提醒。",
                new { type = "object", properties = new
                {
                    operations = new
                    {
                        type = "array",
                        description = "操作数组，按顺序逐项执行",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                action = new
                                {
                                    type = "string",
                                    description = "操作类型",
                                    @enum = new[] { "add", "create", "update", "edit", "delete", "remove", "complete", "uncomplete" }
                                },
                                id = new { type = "string", description = "任务 id(update/delete/complete/uncomplete 必填)" },
                                title = new { type = "string", description = "标题(add 必填；update 可选)" },
                                date = new { type = "string", description = "日期 YYYY-MM-DD" },
                                time = new { type = "string", description = "时刻 HH:mm；null/空串 = 不设时刻" },
                                isImportant = new { type = "boolean", description = "是否重要" },
                                reminders = new
                                {
                                    type = "array",
                                    description = "提醒档位标签；[] = 不提醒",
                                    items = new { type = "string", @enum = ReminderEnum }
                                }
                            },
                            required = new[] { "action" }
                        }
                    }
                }, required = new[] { "operations" } }),
            Tool("list_ai_models", "列出已配置的 OpenAI 兼容 API 上可用的模型（GET /models）。",
                new { type = "object", properties = new { } }),
            Tool("test_ai_connection", "测试已配置的 OpenAI 兼容 API 连接是否可用。",
                new { type = "object", properties = new { } }),
            Tool("ai_chat", "与 AI 对话并用自然语言增删改查任务。模型会通过 function calling 自动调用任务工具（query/add/update/delete/complete/周期任务等），返回最终文本回复。",
                new { type = "object", properties = new
                {
                    message = new { type = "string", description = "要发给 AI 的消息（自然语言）" }
                }, required = new[] { "message" } })
        };
    }

    private static ToolDefinition Tool(string name, string description, object inputSchema)
    {
        return new ToolDefinition(name, description, inputSchema);
    }

    private static async Task WriteJsonRpcResultAsync(HttpListenerResponse response, string id, object result)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteJsonRpcErrorAsync(HttpListenerResponse response, string? id, int code, string message)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private bool CheckAuth(HttpListenerRequest request)
    {
        // 检查 Authorization: Bearer <token>
        var authHeader = request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            var bearer = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader["Bearer ".Length..].Trim()
                : authHeader.Trim();
            if (AuthUtil.TokensEqual(bearer, _token))
            {
                return true;
            }
        }

        // 检查 X-Auth-Token
        return AuthUtil.TokensEqual(request.Headers["X-Auth-Token"], _token);
    }

    /// <summary>Origin 是否指向本机（127.0.0.1 / localhost / [::1] / null）。</summary>
    private static bool IsLocalOrigin(string origin)
    {
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                   || host == "127.0.0.1"
                   || host == "[::1]"
                   || host == "::1";
        }

        return false;
    }

    public void Dispose()
    {
        Stop();

        try
        {
            _listener.Close();
        }
        catch
        {
            // 忽略关闭异常
        }

        try
        {
            _cts?.Dispose();
        }
        catch
        {
            // 忽略释放异常
        }

        _cts = null;

        // 释放懒加载的 AI 助手（内部持有 OpenAiClient 的 HttpClient）。
        _aiAgent?.Dispose();
        _aiAgent = null;
    }
}
