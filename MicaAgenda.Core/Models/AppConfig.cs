namespace MicaAgenda.App.Models;

/// <summary>
/// 应用级配置（独立于日历数据），存放 API、提醒、桌面嵌入等设置。
/// </summary>
public sealed class AppConfig
{
    /// <summary>是否开放 HTTP API（默认开启）。</summary>
    public bool ApiEnabled { get; set; } = true;

    /// <summary>API 监听端口。</summary>
    public int ApiPort { get; set; } = 17803;

    /// <summary>API 访问 Token（为空时首次运行自动生成）。</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>是否开放 MCP 接口（供外部 AI 客户端连接）。</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>MCP 监听端口。</summary>
    public int McpPort { get; set; } = 17804;

    /// <summary>是否启用与 my-mindmap agent 的复习计划同步（勾选状态由 my-mindmap agent 通过 MCP 轮询回写）。</summary>
    public bool SyncMyMindMapEnabled { get; set; } = false;

    /// <summary>my-mindmap agent 本地 HTTP Token（用于桌面端主动推送复习状态）。</summary>
    public string MyMindMapToken { get; set; } = string.Empty;

    /// <summary>my-mindmap agent 本地 HTTP 服务地址（默认端口 17800，与程序内置 HTTP API 一致）。</summary>
    public string MindMapBaseUrl { get; set; } = "http://127.0.0.1:17800";

    /// <summary>是否启用定时提醒（默认关闭，需配置 webhook 后开启）。</summary>
    public bool ReminderEnabled { get; set; }

    /// <summary>全局提醒时间，格式 HH:mm。</summary>
    public string ReminderTime { get; set; } = "09:00";

    /// <summary>
    /// 是否启用「逾期预警」（当天还有未完成任务时，在跨日之前提醒一次）。
    ///
    /// <para>逾期口径：跨过**任务当日的 24:00** 才算逾期。所以"今天"的任务即便时刻已经过了也仍是
    /// 当天待办，这条预警的意义就在于别让当天没做完的任务悄无声息地变成逾期欠账。</para>
    /// </summary>
    public bool OverdueWarnEnabled { get; set; } = true;

    /// <summary>「逾期预警」发送时间，格式 HH:mm。默认 21:00（跨日前留出处理时间）。</summary>
    public string OverdueWarnTime { get; set; } = "21:00";

    /// <summary>飞书机器人 webhook 地址。</summary>
    public string FeishuWebhook { get; set; } = string.Empty;

    /// <summary>企业微信机器人 webhook 地址。</summary>
    public string WeComWebhook { get; set; } = string.Empty;

    /// <summary>是否启用定时报告（任务完成情况周报 / 月报）。</summary>
    public bool ReportEnabled { get; set; }

    /// <summary>报告周期："Weekly"（每周）或 "Monthly"（每月）。</summary>
    public string ReportSchedule { get; set; } = "Weekly";

    /// <summary>报告发送时间，格式 HH:mm。</summary>
    public string ReportTime { get; set; } = "18:00";

    /// <summary>
    /// 周报在星期几发送。1=周一 … 7=周日。
    /// （与 .NET DayOfWeek 的 0=周日 约定不同，这里用 1..7 更贴近中文习惯，
    ///   转换统一在 ReportService 里做，避免各处各写一份。）
    /// </summary>
    public int ReportDayOfWeek { get; set; } = 5;

    /// <summary>月报在几号发送。超过当月天数时自动取当月最后一天。</summary>
    public int ReportDayOfMonth { get; set; } = 1;

    /// <summary>报告是否发送到飞书。</summary>
    public bool ReportSendToFeishu { get; set; }

    /// <summary>报告是否发送到企业微信。</summary>
    public bool ReportSendToWeCom { get; set; }

    /// <summary>
    /// 额外的自定义 webhook（飞书地址自动使用卡片；其他地址按企业微信 text 格式推送）。
    /// 用于接 Server 酱 / PushPlus 这类把消息转发到个人微信的中转服务。
    /// </summary>
    public string ReportCustomWebhook { get; set; } = string.Empty;

    /// <summary>是否启用自动备份（定时导出全年任务 JSON 并推送）。</summary>
    public bool BackupEnabled { get; set; }

    /// <summary>自动备份时间，格式 HH:mm。</summary>
    public string BackupTime { get; set; } = "23:00";

    /// <summary>是否启用备份后自动发送到飞书（需填写飞书 webhook 地址）。</summary>
    public bool BackupSendToFeishu { get; set; }

    /// <summary>是否启用备份后自动发送到企业微信（需填写企微 webhook 地址）。</summary>
    public bool BackupSendToWeCom { get; set; }

    /// <summary>是否锁定窗口位置（禁止拖动/缩放）。</summary>
    public bool LockWindow { get; set; } = false;

    /// <summary>是否开机自启动。</summary>
    public bool AutoStart { get; set; }

    /// <summary>是否以高优先级开机启动。</summary>
    public bool HighPriorityStartup { get; set; }

    /// <summary>是否嵌入桌面（置底模式：沉在其他窗口之下）。默认关闭：
    /// 开启后窗口常驻 HWND_BOTTOM，鼠标 hover 不到就点不到，对新用户
    // 极不友好（看着像"无响应"）；需要的人去设置里勾选即可。</summary>
    public bool EmbedDesktop { get; set; } = false;

    /// <summary>启动时自动向 GitHub Releases 检查新版本（默认开启；关掉后只能手动点「检查更新」）。</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>
    /// 「今日内不再提示更新」记住的那一天（本地日期 yyyy-MM-dd）。
    /// 为空或不是今天时照常提示；是今天就静默跳过自动检查的弹窗（手动点「检查更新」仍会提示）。
    /// </summary>
    public string UpdateSkipDate { get; set; } = string.Empty;

    // ===== AI 助手（OpenAI 兼容）配置 =====

    /// <summary>是否启用 AI 助手（顶栏 AI 对话窗 + 自然语言增删改查任务）。</summary>
    public bool AiEnabled { get; set; }

    /// <summary>OpenAI 兼容 API 的基础 URL（如 https://api.openai.com 或 https://xxx/v1，可为完整端点）。</summary>
    public string AiBaseUrl { get; set; } = string.Empty;

    /// <summary>API Key。</summary>
    public string AiApiKey { get; set; } = string.Empty;

    /// <summary>使用的模型名（可点输入框自动检索，也支持手动输入）。</summary>
    public string AiModel { get; set; } = string.Empty;

    /// <summary>是否自动补全链接：开启后若 URL 不完整，自动补全到 /v1/chat/completions。</summary>
    public bool AiAutoCompleteUrl { get; set; } = true;

    /// <summary>
    /// 复制一份配置快照。设置窗口与主窗体共用同一个 AppConfig 实例，保存时是「就地改写」，
    /// 所以主窗体要判断"哪些设置项变了"必须先拿到改写前的快照——否则新旧值永远相同，
    /// 「保存后立即生效」的分支永远不执行（只能重启程序才生效）。
    /// </summary>
    public AppConfig Clone() => (AppConfig)MemberwiseClone();
}
