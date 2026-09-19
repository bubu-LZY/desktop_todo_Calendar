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
    ///
    /// 第二版（本次）：不再只给「今天/明天/后天/本周一」四个锚点就完事。
    /// 实测翻车案例：用户说「下周二下午一点」，模型自己心算成了再下个周二（9/29），
    /// 正确是 9/22 —— 四个锚点不足以让模型可靠地推「下周X / 下个月X号 / N 天后」。
    ///
    /// 所以这里做三件事：
    /// 1) 把相对日期的**定义**写清楚（「下周X」= 下一个自然周的周X，不是「再下一周」）；
    /// 2) 把常用锚点全部列出（明天/后天/本周一~周日/下周一~周日），让模型有表可查；
    /// 3) 明确要求「拿不准就用 compute_date 工具算」，别心算。
    /// </summary>
    public static string BuildSystemPrompt()
    {
        var now = DateTime.Now;
        var today = now.Date;
        var dow = (int)today.DayOfWeek;                    // 0=周日 … 6=周六

        // 自然周口径：周一为一周起点。周日的 dow=0 要回退 6 天。
        var thisMonday = today.AddDays(dow == 0 ? -6 : 1 - dow);

        var sb = new System.Text.StringBuilder();
        sb.Append("你是日历任务助手。用户用自然语言描述需求，你通过工具完成任务的查询、新增、编辑、删除、标记完成以及周期任务等操作，并用简洁的中文回复结果。\n");
        sb.Append($"当前时间：{now:yyyy-MM-dd HH:mm dddd}。\n");

        // 逐日列出本周与下周的每一天，让模型查表而不是心算。
        sb.Append("本周（周一→周日）：");
        sb.Append(string.Join("、", Enumerable.Range(0, 7)
            .Select(i => $"{WeekdayLabel(i)}{thisMonday.AddDays(i):yyyy-MM-dd}")));
        sb.Append('\n');

        var nextMonday = thisMonday.AddDays(7);
        sb.Append("下周（周一→周日）：");
        sb.Append(string.Join("、", Enumerable.Range(0, 7)
            .Select(i => $"{WeekdayLabel(i)}{nextMonday.AddDays(i):yyyy-MM-dd}")));
        sb.Append('\n');

        sb.Append($"今天={today:yyyy-MM-dd}；明天={today.AddDays(1):yyyy-MM-dd}；后天={today.AddDays(2):yyyy-MM-dd}；");
        sb.Append($"本月底={new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)):yyyy-MM-dd}；");
        sb.Append($"下月同日={today.AddMonths(1):yyyy-MM-dd}。\n");

        sb.Append(
            "相对日期换算规则（必须严格遵守）：\n" +
            "1) 「本周X」= 本周的星期X；「下周X」= 下一个自然周的星期X（注意：若今天是周六/周日，说「下周X」指的是从下周一算起那一周，而不是「今天之后再往后一周」）；\n" +
            "2) 「X天后 / 下周 / 下个月」一律基于上面的锚点表推算，不要凭记忆猜年份或月份；\n" +
            "3) 任何相对时间都必须先换算成绝对日期（YYYY-MM-DD）再调用工具；\n" +
            "4) 拿不准就调用 compute_date 工具来算，禁止心算；换算结果如有歧义，在回复里说明你算出的日期。\n");
        sb.Append("改完/新增后请以工具返回的 verified 字段为准确认是否真的写入成功。\n");
        return sb.ToString();
    }

    private static string WeekdayLabel(int mondayBasedIndex)
        => mondayBasedIndex switch
        {
            0 => "周一",
            1 => "周二",
            2 => "周三",
            3 => "周四",
            4 => "周五",
            5 => "周六",
            _ => "周日",
        };

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
