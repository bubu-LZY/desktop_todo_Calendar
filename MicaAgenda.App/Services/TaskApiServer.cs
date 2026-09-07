using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 内置 HTTP API 服务，暴露任务增删改查与周期查询接口，供其他 Agent 调用。
/// 使用 BCL 自带 HttpListener，无第三方依赖。
/// </summary>
public sealed class TaskApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly CalendarData _data;
    private readonly object _syncRoot;
    private readonly string _token;
    private readonly Action _onDataChanged;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public TaskApiServer(CalendarData data, object syncRoot, string token, Action onDataChanged)
    {
        _data = data;
        _syncRoot = syncRoot;
        _token = token;
        _onDataChanged = onDataChanged;
    }

    public int Port { get; private set; } = 17801;

    public IReadOnlyList<string> ActivePrefixes => _listener.Prefixes.Cast<string>().ToArray();

    /// <summary>启动服务，返回是否成功。</summary>
    public bool Start(int port)
    {
        Port = port;
        // localhost 前缀在非管理员下可用；127.0.0.1 需要 urlacl，尝试注册失败则忽略。
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
        // 观察后台循环任务：即便有遗漏的异常（如 listener 已释放时 GetContextAsync 竞态），
        // 也不会变成"未观察异常"被 finalizer 反复打印到日志。
        _ = _runTask.ContinueWith(
            t => App.LogError(t.Exception?.Flatten().InnerException, "TaskApiServer.RunLoopTask"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    public void Stop()
    {
        // Stop 与 Close 分别兜底：若 Stop 抛异常也要确保 Close 执行，否则监听句柄会泄漏
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
                App.LogError(ex, "TaskApiServer.RunLoop");
                break;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            await DispatchAsync(context);
        }
        catch (Exception ex)
        {
            // 写错误响应本身也可能失败（客户端已断开 / 响应已发送），必须再次兜底，
            // 否则异常会逃逸成未观察任务异常。
            try
            {
                await WriteJsonAsync(context.Response, 500, new { error = ex.Message });
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
                // 忽略响应关闭失败（客户端断开等）
            }
        }
    }

    private async Task DispatchAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // CORS 收敛到本机 origin：API 设计上只服务本机进程，
        // 设成 "*" 会让任意网页都能跨源调用，无端扩大攻击面。
        var origin = request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin)
            && !IsLocalOrigin(origin)
            && !string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(response, 403, new { error = "forbidden origin" });
            return;
        }

        if (!string.IsNullOrEmpty(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Vary"] = "Origin";
        }
        response.Headers["Access-Control-Allow-Headers"] = "Authorization, X-Auth-Token, Content-Type";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            return;
        }

        // 鉴权
        if (!IsAuthorized(request))
        {
            await WriteJsonAsync(response, 401, new { error = "unauthorized: missing or invalid token" });
            return;
        }

        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "/";
        var method = request.HttpMethod.ToUpperInvariant();

        if (path == "/api/health" && method == "GET")
        {
            await WriteJsonAsync(response, 200, new { status = "ok", time = DateTimeOffset.Now });
            return;
        }

        if (path == "/api/tasks")
        {
            switch (method)
            {
                case "GET":
                    await HandleQueryAsync(response, request.QueryString);
                    return;
                case "POST":
                    await HandleAddAsync(response, request);
                    return;
                case "PUT":
                    await HandleBatchAsync(response, request);
                    return;
            }
        }

        if (path == "/api/tasks/bulk-complete" && method == "POST")
        {
            await HandleBulkCompleteAsync(response, request);
            return;
        }

        if (path.StartsWith("/api/tasks/", StringComparison.Ordinal))
        {
            await HandleTaskByIdAsync(response, request, path);
            return;
        }

        await WriteJsonAsync(response, 404, new { error = "not found" });
    }

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

    private bool IsAuthorized(HttpListenerRequest request)
    {
        // Token 必须存在：未配置 token 时**直接拒绝**，不再保留"无 token 裸奔"的旁路。
        if (string.IsNullOrWhiteSpace(_token))
        {
            return false;
        }

        var header = request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(header))
        {
            var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..].Trim()
                : header.Trim();
            if (AuthUtil.TokensEqual(bearer, _token))
            {
                return true;
            }
        }

        return AuthUtil.TokensEqual(request.Headers["X-Auth-Token"], _token);
    }

    private Task HandleQueryAsync(HttpListenerResponse response, System.Collections.Specialized.NameValueCollection query)
    {
        var range = query["range"]?.ToLowerInvariant() ?? "today";
        var dateStr = query["date"];
        DateOnly? exactDate = dateStr is not null && DateOnly.TryParse(dateStr, out var d) ? d : null;

        var tasks = QueryTasks(range, exactDate);
        return WriteJsonAsync(response, 200, new
        {
            range,
            count = tasks.Count,
            tasks = tasks.Select(ToDto)
        });
    }

    private async Task HandleAddAsync(HttpListenerResponse response, HttpListenerRequest request)
    {
        var payload = await ReadJsonAsync<AddTaskRequest>(request);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Title))
        {
            await WriteJsonAsync(response, 400, new { error = "title is required" });
            return;
        }

        var date = payload.Date ?? DateOnly.FromDateTime(DateTime.Now);
        var task = new CalendarTask
        {
            Date = date,
            Title = payload.Title.Trim(),
            IsImportant = payload.IsImportant ?? false,
            CreatedAt = DateTimeOffset.Now
        };

        lock (_syncRoot)
        {
            _data.Tasks.Add(task);
        }

        _onDataChanged();
        await WriteJsonAsync(response, 200, ToDto(task));
    }

    private async Task HandleBatchAsync(HttpListenerResponse response, HttpListenerRequest request)
    {
        var payload = await ReadJsonAsync<BatchRequest>(request);
        if (payload?.Operations is null || payload.Operations.Count == 0)
        {
            await WriteJsonAsync(response, 400, new { error = "operations is required" });
            return;
        }

        var results = new List<object>();
        lock (_syncRoot)
        {
            foreach (var op in payload.Operations)
            {
                results.Add(ApplyOperation(op));
            }
        }

        _onDataChanged();
        await WriteJsonAsync(response, 200, new { results });
    }

    /// <summary>
    /// 批量设置完成状态：按 id 列表、单个 id、或按日期/日期列表匹配到的所有任务。
    /// 用于"当日多任务 / 多日多任务 / 指定日期多任务"一次性标记完成或取消。
    /// </summary>
    private async Task HandleBulkCompleteAsync(HttpListenerResponse response, HttpListenerRequest request)
    {
        var payload = await ReadJsonAsync<BulkCompleteRequest>(request);
        if (payload is null)
        {
            await WriteJsonAsync(response, 400, new { error = "invalid body" });
            return;
        }

        var completed = payload.Completed ?? true;
        var ids = payload.Ids ?? new List<Guid>();
        if (payload.Id is not null)
        {
            ids.Add(payload.Id.Value);
        }

        var dates = payload.Dates ?? new List<DateOnly>();
        if (payload.Date is not null)
        {
            dates.Add(payload.Date.Value);
        }

        var matched = new List<CalendarTask>();
        lock (_syncRoot)
        {
            foreach (var task in _data.Tasks)
            {
                if (ids.Contains(task.Id))
                {
                    matched.Add(task);
                }
                else if (dates.Count > 0 && dates.Contains(task.Date))
                {
                    matched.Add(task);
                }
            }

            foreach (var task in matched)
            {
                if (completed)
                {
                    task.MarkCompleted(DateTimeOffset.Now);
                }
                else
                {
                    task.MarkIncomplete();
                }
            }
        }

        _onDataChanged();
        await WriteJsonAsync(response, 200, new
        {
            completed,
            affected = matched.Count,
            tasks = matched.Select(ToDto)
        });
    }

    private object ApplyOperation(BatchOperation op)
    {
        try
        {
            return op.Action?.ToLowerInvariant() switch
            {
                "add" or "create" => AddOperation(op),
                "update" or "edit" => UpdateOperation(op),
                "delete" or "remove" => DeleteOperation(op),
                "complete" => SetCompletionOperation(op, true),
                "uncomplete" or "incomplete" => SetCompletionOperation(op, false),
                _ => new { error = $"unknown action: {op.Action}" }
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object AddOperation(BatchOperation op)
    {
        if (string.IsNullOrWhiteSpace(op.Title))
        {
            throw new ArgumentException("title is required");
        }

        var task = new CalendarTask
        {
            Date = op.Date ?? DateOnly.FromDateTime(DateTime.Now),
            Title = op.Title.Trim(),
            IsImportant = op.IsImportant ?? false,
            CreatedAt = DateTimeOffset.Now
        };
        _data.Tasks.Add(task);
        return ToDto(task);
    }

    private object UpdateOperation(BatchOperation op)
    {
        var task = FindTask(op.Id ?? Guid.Empty);
        if (task is null)
        {
            throw new KeyNotFoundException($"task not found: {op.Id}");
        }

        if (op.Title is not null)
        {
            task.Title = op.Title.Trim();
        }

        if (op.Date is not null)
        {
            task.Date = op.Date.Value;
        }

        if (op.IsImportant is not null)
        {
            task.IsImportant = op.IsImportant.Value;
        }

        return ToDto(task);
    }

    private object DeleteOperation(BatchOperation op)
    {
        var task = FindTask(op.Id ?? Guid.Empty);
        if (task is null)
        {
            throw new KeyNotFoundException($"task not found: {op.Id}");
        }

        _data.Tasks.Remove(task);
        return new { id = task.Id, deleted = true };
    }

    private object SetCompletionOperation(BatchOperation op, bool completed)
    {
        var task = FindTask(op.Id ?? Guid.Empty);
        if (task is null)
        {
            throw new KeyNotFoundException($"task not found: {op.Id}");
        }

        if (completed)
        {
            task.MarkCompleted(DateTimeOffset.Now);
        }
        else
        {
            task.MarkIncomplete();
        }

        return ToDto(task);
    }

    private async Task HandleTaskByIdAsync(HttpListenerResponse response, HttpListenerRequest request, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // 期望 /api/tasks/{id} 或 /api/tasks/{id}/{action}
        if (segments.Length < 3 || !Guid.TryParse(segments[2], out var id))
        {
            await WriteJsonAsync(response, 400, new { error = "invalid task id" });
            return;
        }

        var action = segments.Length >= 4 ? segments[3].ToLowerInvariant() : string.Empty;
        var method = request.HttpMethod.ToUpperInvariant();

        if (method == "GET" && action == string.Empty)
        {
            var task = FindTask(id);
            if (task is null)
            {
                await WriteJsonAsync(response, 404, new { error = "task not found" });
                return;
            }

            await WriteJsonAsync(response, 200, ToDto(task));
            return;
        }

        if (method == "DELETE" && action == string.Empty)
        {
            // 绝不能在 lock 内同步等待网络 I/O（GetAwaiter().GetResult()）：
            // 客户端读得慢时响应写不出去，锁就一直不释放，会与 UI 线程互等造成死锁。
            bool deleted;
            lock (_syncRoot)
            {
                var task = FindTask(id);
                deleted = task is not null;
                if (deleted)
                {
                    _data.Tasks.Remove(task!);
                }
            }

            if (!deleted)
            {
                await WriteJsonAsync(response, 404, new { error = "task not found" });
                return;
            }

            _onDataChanged();
            await WriteJsonAsync(response, 200, new { id, deleted = true });
            return;
        }

        if (method == "PUT" && action == string.Empty)
        {
            var payload = await ReadJsonAsync<UpdateTaskRequest>(request);
            if (payload is null)
            {
                await WriteJsonAsync(response, 400, new { error = "invalid body" });
                return;
            }

            CalendarTask? updated;
            lock (_syncRoot)
            {
                var task = FindTask(id);
                if (task is null)
                {
                    updated = null;
                }
                else
                {
                    if (payload.Title is not null)
                    {
                        task.Title = payload.Title.Trim();
                    }

                    if (payload.Date is not null)
                    {
                        task.Date = payload.Date.Value;
                    }

                    if (payload.IsImportant is not null)
                    {
                        task.IsImportant = payload.IsImportant.Value;
                    }

                    updated = task;
                }
            }

            if (updated is null)
            {
                await WriteJsonAsync(response, 404, new { error = "task not found" });
                return;
            }

            _onDataChanged();
            await WriteJsonAsync(response, 200, ToDto(updated));
            return;
        }

        if (method == "POST")
        {
            switch (action)
            {
                case "complete":
                    await SetCompletionAsync(response, id, true);
                    return;
                case "uncomplete":
                case "incomplete":
                    await SetCompletionAsync(response, id, false);
                    return;
            }
        }

        await WriteJsonAsync(response, 404, new { error = "not found" });
    }

    private async Task SetCompletionAsync(HttpListenerResponse response, Guid id, bool completed)
    {
        CalendarTask? updated;
        lock (_syncRoot)
        {
            var task = FindTask(id);
            if (task is null)
            {
                updated = null;
            }
            else
            {
                if (completed)
                {
                    task.MarkCompleted(DateTimeOffset.Now);
                }
                else
                {
                    task.MarkIncomplete();
                }

                updated = task;
            }
        }

        if (updated is null)
        {
            await WriteJsonAsync(response, 404, new { error = "task not found" });
            return;
        }

        _onDataChanged();
        await WriteJsonAsync(response, 200, ToDto(updated));
    }

    private List<CalendarTask> QueryTasks(string range, DateOnly? exactDate)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        lock (_syncRoot)
        {
            if (exactDate is not null)
            {
                return _data.Tasks.Where(t => t.Date == exactDate.Value).ToList();
            }

            return range switch
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

    private CalendarTask? FindTask(Guid id)
    {
        return _data.Tasks.FirstOrDefault(t => t.Id == id);
    }

    private static DateOnly GetWeekStart(DateOnly date)
    {
        return date.AddDays(-(int)date.DayOfWeek);
    }

    private static object ToDto(CalendarTask task)
    {
        return new
        {
            id = task.Id,
            date = task.Date.ToString("yyyy-MM-dd"),
            title = task.Title,
            isCompleted = task.IsCompleted,
            isImportant = task.IsImportant,
            createdAt = task.CreatedAt,
            completedAt = task.CompletedAt
        };
    }

    /// <summary>单次请求 body 上限 1 MB，超出直接拒。</summary>
    private const long MaxRequestBodyBytes = 1L * 1024 * 1024;

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request)
        where T : class
    {
        if (!request.HasEntityBody)
        {
            return null;
        }

        // 提前检查 Content-Length，避免被无 Content-Length 的超大请求耗光内存。
        // 客户端伪造 Content-Length 撒谎时，底层流截断会抛 IOException，落到调用方统一处理。
        if (request.ContentLength64 > MaxRequestBodyBytes)
        {
            throw new InvalidDataException($"request body too large: {request.ContentLength64} > {MaxRequestBodyBytes}");
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // 客户端没声明 Content-Length 时的兜底：读完后再判一次字节数
        if (System.Text.Encoding.UTF8.GetByteCount(body) > MaxRequestBodyBytes)
        {
            throw new InvalidDataException("request body too large");
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
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

    private sealed class AddTaskRequest
    {
        public DateOnly? Date { get; set; }
        public string? Title { get; set; }
        public bool? IsImportant { get; set; }
    }

    private sealed class UpdateTaskRequest
    {
        public string? Title { get; set; }
        public DateOnly? Date { get; set; }
        public bool? IsImportant { get; set; }
    }

    private sealed class BatchRequest
    {
        public List<BatchOperation>? Operations { get; set; }
    }

    private sealed class BatchOperation
    {
        public string? Action { get; set; }
        public Guid? Id { get; set; }
        public DateOnly? Date { get; set; }
        public string? Title { get; set; }
        public bool? IsImportant { get; set; }
    }

    private sealed class BulkCompleteRequest
    {
        public bool? Completed { get; set; }
        public Guid? Id { get; set; }
        public List<Guid>? Ids { get; set; }
        public DateOnly? Date { get; set; }
        public List<DateOnly>? Dates { get; set; }
    }
}
