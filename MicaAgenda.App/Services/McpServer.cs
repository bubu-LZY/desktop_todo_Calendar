using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 内置 MCP Server（Model Context Protocol），采用 Streamable HTTP transport。
/// 外部 AI 客户端（Claude Desktop / Cursor 等）可通过
/// http://localhost:&lt;port&gt;/mcp 连接，调用任务增删改查等工具。
/// </summary>
public sealed class McpServer : IDisposable
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "micaagenda";
    private const string ServerVersion = "1.4.0";

    private readonly CalendarData _data;
    private readonly object _syncRoot;
    private readonly Action _onDataChanged;
    private readonly HttpListener _listener = new();
    private readonly string _token;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public McpServer(CalendarData data, object syncRoot, Action onDataChanged, string token)
    {
        _data = data;
        _syncRoot = syncRoot;
        _onDataChanged = onDataChanged;
        _token = token ?? string.Empty;
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
            t => App.LogError(t.Exception?.Flatten().InnerException, "McpServer.RunLoopTask"),
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
                App.LogError(ex, "McpServer.RunLoop");
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
            "query_tasks" => QueryTasks(GetArgString(args, "range"), GetArgDate(args, "date")),
            "add_task" => AddTask(GetArgDate(args, "date"), GetArgString(args, "title"), GetArgBool(args, "isImportant")),
            "update_task" => UpdateTask(GetArgGuid(args, "id"), args),
            "delete_task" => DeleteTask(GetArgGuid(args, "id")),
            "complete_task" => SetCompletion(GetArgGuid(args, "id"), true),
            "uncomplete_task" => SetCompletion(GetArgGuid(args, "id"), false),
            "batch_tasks" => BatchTasks(args),
            _ => throw new ArgumentException($"unknown tool: {name}")
        };
    }

    private object QueryTasks(string? range, DateOnly? exactDate)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        List<CalendarTask> tasks;
        lock (_syncRoot)
        {
            if (exactDate is not null)
            {
                tasks = _data.Tasks.Where(t => t.Date == exactDate.Value).ToList();
            }
            else
            {
                tasks = (range ?? "today") switch
                {
                    "today" => _data.Tasks.Where(t => t.Date == today).ToList(),
                    "week" => _data.Tasks.Where(t => t.Date >= GetWeekStart(today) && t.Date <= GetWeekStart(today).AddDays(6)).ToList(),
                    "month" => _data.Tasks.Where(t => t.Date.Year == today.Year && t.Date.Month == today.Month).ToList(),
                    "year" => _data.Tasks.Where(t => t.Date.Year == today.Year).ToList(),
                    "all" => _data.Tasks.ToList(),
                    _ => _data.Tasks.Where(t => t.Date == today).ToList()
                };
            }
        }

        return new
        {
            range = range ?? "today",
            count = tasks.Count,
            tasks = tasks.OrderBy(t => t.Date).Select(ToDto)
        };
    }

    private object AddTask(DateOnly? date, string? title, bool? isImportant)
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
            CreatedAt = DateTimeOffset.Now
        };

        lock (_syncRoot)
        {
            _data.Tasks.Add(task);
        }

        _onDataChanged();
        return ToDto(task);
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

            if (TryGetArg(args, "date", out var dateEl) && DateOnly.TryParse(dateEl.GetString(), out var d))
            {
                task.Date = d;
            }

            if (TryGetArg(args, "isImportant", out var impEl) && impEl.ValueKind == JsonValueKind.True)
            {
                task.IsImportant = true;
            }
            else if (TryGetArg(args, "isImportant", out impEl) && impEl.ValueKind == JsonValueKind.False)
            {
                task.IsImportant = false;
            }
        }

        _onDataChanged();
        lock (_syncRoot)
        {
            return ToDto(_data.Tasks.First(t => t.Id == id));
        }
    }

    private object DeleteTask(Guid id)
    {
        lock (_syncRoot)
        {
            var task = _data.Tasks.FirstOrDefault(t => t.Id == id)
                ?? throw new KeyNotFoundException($"task not found: {id}");
            _data.Tasks.Remove(task);
        }

        _onDataChanged();
        return new { id, deleted = true };
    }

    private object SetCompletion(Guid id, bool completed)
    {
        lock (_syncRoot)
        {
            var task = _data.Tasks.FirstOrDefault(t => t.Id == id)
                ?? throw new KeyNotFoundException($"task not found: {id}");

            if (completed)
            {
                task.MarkCompleted(DateTimeOffset.Now);
            }
            else
            {
                task.MarkIncomplete();
            }
        }

        _onDataChanged();
        lock (_syncRoot)
        {
            return ToDto(_data.Tasks.First(t => t.Id == id));
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
                results.Add(action?.ToLowerInvariant() switch
                {
                    "add" or "create" => AddTask(opDate, opTitle, opImportant),
                    "update" or "edit" => UpdateTaskById(opId ?? Guid.Empty, opTitle, opDate, opImportant),
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

    private object UpdateTaskById(Guid id, string? title, DateOnly? date, bool? isImportant)
    {
        lock (_syncRoot)
        {
            var task = _data.Tasks.FirstOrDefault(t => t.Id == id)
                ?? throw new KeyNotFoundException($"task not found: {id}");

            if (title is not null)
            {
                task.Title = title.Trim();
            }

            if (date is not null)
            {
                task.Date = date.Value;
            }

            if (isImportant is not null)
            {
                task.IsImportant = isImportant.Value;
            }
        }

        _onDataChanged();
        lock (_syncRoot)
        {
            return ToDto(_data.Tasks.First(t => t.Id == id));
        }
    }

    private static DateOnly GetWeekStart(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    private static object ToDto(CalendarTask task) => new
    {
        id = task.Id,
        date = task.Date.ToString("yyyy-MM-dd"),
        title = task.Title,
        isCompleted = task.IsCompleted,
        isImportant = task.IsImportant,
        createdAt = task.CreatedAt,
        completedAt = task.CompletedAt,
        // 状态最后变更时间（完成和取消完成都会刷新），供对端做时间戳仲裁
        updatedAt = task.UpdatedAt
    };

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

    private static object[] GetToolDefinitions()
    {
        return new object[]
        {
            Tool("query_tasks", "查询任务清单。range 可选 today(今日,默认)/week(本周)/month(本月)/year(本年)/all(全部)；date 可选，指定某一天(YYYY-MM-DD)。",
                new { type = "object", properties = new
                {
                    range = new { type = "string", description = "查询范围", @enum = new[] { "today", "week", "month", "year", "all" } },
                    date = new { type = "string", description = "指定日期 YYYY-MM-DD" }
                } }),
            Tool("add_task", "在某一天添加任务。date 省略默认今天。",
                new { type = "object", properties = new
                {
                    title = new { type = "string", description = "任务标题" },
                    date = new { type = "string", description = "日期 YYYY-MM-DD" },
                    isImportant = new { type = "boolean", description = "是否重要" }
                }, required = new[] { "title" } }),
            Tool("update_task", "编辑任务。id 必填，title/date/isImportant 可选。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "任务 id" },
                    title = new { type = "string", description = "新标题" },
                    date = new { type = "string", description = "新日期 YYYY-MM-DD" },
                    isImportant = new { type = "boolean", description = "是否重要" }
                }, required = new[] { "id" } }),
            Tool("delete_task", "删除任务。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "任务 id" }
                }, required = new[] { "id" } }),
            Tool("complete_task", "标记任务为已完成。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "任务 id" }
                }, required = new[] { "id" } }),
            Tool("uncomplete_task", "取消任务的完成状态。",
                new { type = "object", properties = new
                {
                    id = new { type = "string", description = "任务 id" }
                }, required = new[] { "id" } }),
            Tool("batch_tasks", "批量增删改查任务。operations 为操作数组，每项 action 可取 add/create、update/edit、delete/remove、complete、uncomplete。",
                new { type = "object", properties = new
                {
                    operations = new { type = "array", description = "操作数组" }
                }, required = new[] { "operations" } })
        };
    }

    private static object Tool(string name, string description, object inputSchema)
    {
        return new { name, description, inputSchema };
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
    }
}
