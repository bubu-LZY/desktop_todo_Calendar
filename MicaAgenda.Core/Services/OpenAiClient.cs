using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>一次 chat 响应的工具调用（function calling）。</summary>
public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>一次 chat 的返回：要么有文本，要么有工具调用（也可能两者皆空）。</summary>
public sealed record AiChatResponse(string? Content, List<AiToolCall> ToolCalls)
{
    public static AiChatResponse Text(string content) => new(content, []);
    public static AiChatResponse Calls(List<AiToolCall> calls) => new(null, calls);
}

/// <summary>对话消息。role = user / assistant / system / tool；
/// tool 消息用 ToolCallId；assistant 消息带工具调用时用 ToolCalls（原始 tool_calls 数组结构）。</summary>
public sealed record AiChatMessage(
    string Role,
    string? Content = null,
    string? ToolCallId = null,
    IReadOnlyList<object>? ToolCalls = null);

/// <summary>
/// OpenAI 兼容 API 客户端（只依赖 BCL HttpClient + System.Text.Json，无第三方依赖）。
/// 支持：列出模型（GET /models）、测试连接、chat completions 与 function calling。
/// </summary>
public sealed class OpenAiClient : IDisposable
{
    private readonly HttpClient _http;

    public OpenAiClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    // ===== URL 规范化 =====

    /// <summary>
    /// 规范化 chat 端点。autoComplete 开启时把不完整的 base URL 补全到 /v1/chat/completions：
    ///   https://api.openai.com          → https://api.openai.com/v1/chat/completions
    ///   https://api.openai.com/v1       → https://api.openai.com/v1/chat/completions
    ///   https://api.openai.com/v1/chat/completions → 原样（已完整）
    /// 关闭时原样返回（允许用户填完全自定义的端点，如国内代理的 /v1/chat/completions 变体）。
    /// </summary>
    public static string NormalizeChatUrl(string? baseUrl, bool autoComplete)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            return string.Empty;
        }

        if (!autoComplete)
        {
            return url;
        }

        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return url + "/chat/completions";
        }

        return url + "/v1/chat/completions";
    }

    /// <summary>从任意 base URL 推导 /models 端点（供列出模型用）。</summary>
    public static string NormalizeModelsUrl(string? baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            return string.Empty;
        }

        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/chat/completions".Length];
        }
        else if (url.EndsWith("/completions", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/completions".Length];
        }

        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? url + "/models"
            : url + "/v1/models";
    }

    // ===== HTTP 调用 =====

    /// <summary>列出可用模型（GET /models），按 id 排序去重。</summary>
    public async Task<List<string>> ListModelsAsync(string baseUrl, string apiKey)
    {
        var url = NormalizeModelsUrl(baseUrl);
        if (url.Length == 0)
        {
            throw new InvalidOperationException("API URL 为空。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(request, apiKey);
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"列出模型失败（{(int)response.StatusCode}）：{Truncate(body, 300)}");
        }

        using var doc = JsonDocument.Parse(body);
        var models = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    models.Add(id.GetString()!);
                }
            }
        }

        return models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>测试连接：先试 /models，失败则发一个最小 chat 请求。返回可读的结果描述。</summary>
    public async Task<string> TestConnectionAsync(string baseUrl, string apiKey, string? model)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "未填写 API Key。";
        }

        if (NormalizeChatUrl(baseUrl, autoComplete: false).Length == 0)
        {
            return "API URL 为空。";
        }

        try
        {
            var models = await ListModelsAsync(baseUrl, apiKey);
            return $"连接成功，发现 {models.Count} 个模型" + (models.Count > 0 ? $"（如 {models[0]}）" : string.Empty) + "。";
        }
        catch (Exception modelsEx)
        {
            // /models 不可用（部分代理不实现），退回最小 chat 请求再试一次
            try
            {
                var m = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model!;
                var chatUrl = NormalizeChatUrl(baseUrl, autoComplete: true);
                await SendChatAsync(chatUrl, apiKey, m, [new AiChatMessage("user", "ping")], null);
                return "连接成功（/models 不可用，但 chat 端点可用）。";
            }
            catch (Exception chatEx)
            {
                return $"连接失败：{Truncate(modelsEx.Message, 200)}；chat 亦失败：{Truncate(chatEx.Message, 200)}";
            }
        }
    }

    /// <summary>
    /// 发一次 chat completions 请求。tools 为 function 定义（每个是 { type="function", function={name,description,parameters} }），
    /// 传 null 表示纯文本对话。返回文本或工具调用。
    /// </summary>
    public async Task<AiChatResponse> ChatAsync(
        string baseUrl,
        string apiKey,
        string model,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<object>? tools)
    {
        var url = NormalizeChatUrl(baseUrl, autoComplete: true);
        if (url.Length == 0)
        {
            throw new InvalidOperationException("API URL 为空。");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("未填写 API Key。");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("未填写模型名。");
        }

        return await SendChatAsync(url, apiKey, model, messages, tools);
    }

    private async Task<AiChatResponse> SendChatAsync(
        string url, string apiKey, string model,
        IReadOnlyList<AiChatMessage> messages, IReadOnlyList<object>? tools)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(SerializeMessage).ToList(),
            ["stream"] = false
        };
        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        AddAuth(request, apiKey);

        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"chat 请求失败（{(int)response.StatusCode}）：{Truncate(body, 500)}");
        }

        return ParseChatResponse(body);
    }

    private static object SerializeMessage(AiChatMessage m)
    {
        if (m.Role == "tool")
        {
            return new { role = "tool", content = m.Content ?? string.Empty, tool_call_id = m.ToolCallId ?? string.Empty };
        }

        if (m.ToolCalls is { Count: > 0 })
        {
            return new { role = "assistant", content = m.Content, tool_calls = m.ToolCalls };
        }

        return new { role = m.Role, content = m.Content ?? string.Empty };
    }

    private static AiChatResponse ParseChatResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return AiChatResponse.Text(string.Empty);
        }

        var message = choices[0].TryGetProperty("message", out var msg) ? msg : default;
        if (msg.ValueKind != JsonValueKind.Object)
        {
            return AiChatResponse.Text(string.Empty);
        }

        var content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        var calls = new List<AiToolCall>();
        if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in toolCalls.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : default;
                var name = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("name", out var nEl)
                    ? nEl.GetString() ?? string.Empty
                    : string.Empty;
                var args = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("arguments", out var aEl)
                    ? aEl.GetString() ?? "{}"
                    : "{}";
                calls.Add(new AiToolCall(id, name, args));
            }
        }

        return calls.Count > 0 ? AiChatResponse.Calls(calls) : AiChatResponse.Text(content ?? string.Empty);
    }

    private static void AddAuth(HttpRequestMessage request, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    private static string Truncate(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= max ? text : text[..max] + "…";
    }

    public void Dispose() => _http.Dispose();
}
