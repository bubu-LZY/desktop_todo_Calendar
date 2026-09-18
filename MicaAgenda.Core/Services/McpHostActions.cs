namespace MicaAgenda.App.Services;

/// <summary>
/// 宿主能力桥：MCP / AI 里那些需要访问宿主服务（定时报告、自动备份）的工具，
/// 通过这个委托包注入 —— McpServer 不直接依赖具体服务类型，单测也能替换成假实现。
///
/// 字段全部可空：未注入时对应工具会返回明确的"未就绪"错误，而不是抛 NRE。
/// 注意这些委托在**调用时**才求值，所以宿主可以先构造本对象、后创建服务实例。
/// </summary>
public sealed class McpHostActions
{
    /// <summary>立即发送报告到已配置渠道（飞书卡片 / 企微 markdown / 自定义 webhook）。返回 (是否成功, 说明)。</summary>
    public Func<Task<(bool Sent, string Message)>>? SendReportAsync { get; init; }

    /// <summary>预览当前周期报告文本（不发送）。</summary>
    public Func<string>? PreviewReport { get; init; }

    /// <summary>立即执行一次备份（导出今年任务 JSON，并按配置推送飞书 / 企微）。返回结果说明。</summary>
    public Func<Task<string>>? RunBackupAsync { get; init; }

    /// <summary>
    /// 把备份结果拼成一句可读回执。放在这里而不是各宿主各写一遍，
    /// 保证两个宿主给 AI / MCP 的回执口径一致。
    /// </summary>
    public static string DescribeBackup(BackupResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.LocalPath))
        {
            parts.Add($"已保存到本地：{result.LocalPath}");
        }

        if (result.FeishuSent)
        {
            parts.Add("已推送到飞书");
        }

        if (result.WeComSent)
        {
            parts.Add("已推送到企业微信");
        }

        if (result.Errors.Count > 0)
        {
            parts.Add("失败：" + string.Join("；", result.Errors));
        }

        return parts.Count == 0
            ? "备份完成，但既未生成本地文件也没有推送成功（请检查备份设置）。"
            : string.Join("；", parts);
    }
}
