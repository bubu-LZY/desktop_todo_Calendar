namespace MicaAgenda.App.Models;

/// <summary>
/// 应用级配置（独立于日历数据），存放 API、提醒、桌面嵌入等设置。
/// </summary>
public sealed class AppConfig
{
    /// <summary>是否开放 HTTP API（默认开启）。</summary>
    public bool ApiEnabled { get; set; } = true;

    /// <summary>API 监听端口。</summary>
    public int ApiPort { get; set; } = 17801;

    /// <summary>API 访问 Token（为空时首次运行自动生成）。</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>是否开放 MCP 接口（供外部 AI 客户端连接）。</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>MCP 监听端口。</summary>
    public int McpPort { get; set; } = 17802;

    /// <summary>是否启用定时提醒（默认关闭，需配置 webhook 后开启）。</summary>
    public bool ReminderEnabled { get; set; }

    /// <summary>全局提醒时间，格式 HH:mm。</summary>
    public string ReminderTime { get; set; } = "09:00";

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

}
