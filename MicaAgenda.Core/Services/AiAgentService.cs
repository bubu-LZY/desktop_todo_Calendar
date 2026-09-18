using System.Text.Json;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// AI 助手：把 OpenAI 兼容模型接到任务数据上，通过 function calling 让模型完成
/// 任务的增删改查（复用 MCP 的同一套工具逻辑，保证 AI / MCP / 界面三处口径一致）。
/// </summary>
public sealed class AiAgentService : IDisposable
{
    /// <summary>最多允许的 tool-call 往返轮数（防止模型陷入无限调用循环）。</summary>
    private const int MaxToolRounds = 8;

    private readonly Func<AppConfig> _configProvider;
    private readonly McpServer _toolServer;
    private readonly OpenAiClient _client = new();

    public AiAgentService(
        CalendarData data,
        object syncRoot,
        Func<AppConfig> configProvider,
        Action onDataChanged,
        McpHostActions? hostActions = null)
    {
        _configProvider = configProvider;
        // 不 Start 的 McpServer 只用来复用其工具执行逻辑（InvokeToolForTest），不监听端口。
        // 宿主能力（报告 / 备份）一并透传：AI 对话里也能「发周报 / 立即备份」。
        _toolServer = new McpServer(data, syncRoot, onDataChanged, string.Empty, hostActions: hostActions);
    }

    /// <summary>OpenAI function calling 用的工具定义（复用 MCP 的同一份 schema）。</summary>
    public IReadOnlyList<object> FunctionTools { get; } = BuildFunctionTools();

    /// <summary>
    /// 不暴露给模型自身的元工具：<c>ai_chat</c> 会自我递归（模型调 AI 再调模型），
    /// <c>list_ai_models</c> / <c>test_ai_connection</c> 是配置类工具，与任务增删改查无关。
    /// 它们在 MCP tools/list 里仍然对外可见，只是不进入本进程内 AI 的 function calling 清单。
    /// </summary>
    private static readonly HashSet<string> MetaTools = new(StringComparer.Ordinal)
    {
        "ai_chat", "list_ai_models", "test_ai_connection"
    };

    private static IReadOnlyList<object> BuildFunctionTools()
    {
        var result = new List<object>();
        foreach (var tool in McpServer.GetToolDefinitions())
        {
            if (MetaTools.Contains(tool.Name))
            {
                continue;
            }

            result.Add(new
            {
                type = "function",
                function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema }
            });
        }

        return result;
    }

    /// <summary>
    /// 执行一轮对话。模型可在回复中发起工具调用（function calling），
    /// 这里自动执行工具并把结果回填，循环直到模型给出纯文本回复。
    /// </summary>
    public async Task<string> ChatAsync(IReadOnlyList<AiChatMessage> history)
    {
        var config = _configProvider();
        if (string.IsNullOrWhiteSpace(config.AiBaseUrl)
            || string.IsNullOrWhiteSpace(config.AiApiKey)
            || string.IsNullOrWhiteSpace(config.AiModel))
        {
            return "尚未配置 AI：请先在设置里填写 API URL、API Key 与模型名。";
        }

        // 模型不知道今天是几号：「明天下午3点」曾被解析成 121 天前的日期。
        // 统一在这里注入带当前日期的系统提示，并覆盖调用方可能自带的旧 system。
        var systemPrompt = new AiChatMessage("system", BuildSystemPrompt());
        var messages = new List<AiChatMessage>(history);
        if (messages.Count > 0 && messages[0].Role == "system")
        {
            messages[0] = systemPrompt;
        }
        else
        {
            messages.Insert(0, systemPrompt);
        }

        var userText = history.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var resp = await _client.ChatAsync(config.AiBaseUrl, config.AiApiKey, config.AiModel, messages, FunctionTools);
            if (resp.ToolCalls.Count == 0)
            {
                var reply = string.IsNullOrWhiteSpace(resp.Content) ? "（无回复）" : resp.Content!;
                AiChatLogService.Append("user", userText);
                AiChatLogService.Append("assistant", reply);
                return reply;
            }

            // 把 assistant 的工具调用原样回填（模型需要看到自己发起的 tool_calls）
            var toolCallsObj = resp.ToolCalls.Select(call => (object)new
            {
                id = call.Id,
                type = "function",
                function = new { name = call.Name, arguments = call.ArgumentsJson }
            }).ToList();
            messages.Add(new AiChatMessage("assistant", null, null, toolCallsObj));

            foreach (var call in resp.ToolCalls)
            {
                var resultText = ExecuteTool(call);
                messages.Add(new AiChatMessage("tool", resultText, call.Id));
            }
        }

        var stopped = "工具调用轮次过多，已停止。";
        AiChatLogService.Append("user", userText);
        AiChatLogService.Append("assistant", stopped);
        return stopped;
    }

    /// <summary>
    /// 构建系统提示。关键：必须注入当前日期 —— 模型的训练数据截止之后它不知道「今天」，
    /// 用户说「明天下午3点」时它只能瞎编一个日期（实测被解析成 121 天前）。
    /// </summary>
    public static string BuildSystemPrompt()
    {
        var now = DateTime.Now;
        var today = now.Date;
        // 本周一：周日 DayOfWeek=0 时回退 6 天，否则回退 (dow-1) 天
        var dow = (int)today.DayOfWeek;
        var monday = today.AddDays(dow == 0 ? -6 : 1 - dow);

        return "你是日历任务助手。用户用自然语言描述需求，你通过工具完成任务的查询、新增、编辑、删除、标记完成以及周期任务等操作，并用简洁的中文回复结果。\n" +
               $"当前时间：{now:yyyy-MM-dd HH:mm dddd}。\n" +
               $"今天：{today:yyyy-MM-dd}；明天：{today.AddDays(1):yyyy-MM-dd}；后天：{today.AddDays(2):yyyy-MM-dd}；本周一：{monday:yyyy-MM-dd}。\n" +
               "用户说「今天 / 明天 / 后天 / 下周X」等相对日期时，必须先换算成上面给出的绝对日期（YYYY-MM-DD）再调用工具，绝不要自己猜测年份或月份。";
    }

    private string ExecuteTool(AiToolCall call)
    {
        try
        {
            var result = _toolServer.InvokeToolForTest(call.Name, call.ArgumentsJson);
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public void Dispose() => _client.Dispose();
}
