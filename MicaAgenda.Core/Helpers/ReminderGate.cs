namespace MicaAgenda.App.Helpers;

using MicaAgenda.App.Models;

/// <summary>
/// 「提醒设了也送不出去」的守门人。右侧面板与日期格子里的快速添加共用同一份判断，
/// 免得两处各写一份、慢慢跑偏。
///
/// 提醒是走 webhook 推的（<see cref="AppConfig.FeishuWebhook"/> / <see cref="AppConfig.WeComWebhook"/>），
/// 没配地址的时候用户选「提前 30 分钟」也不会有任何东西响 —— 必须在选的那一刻就说清楚。
/// </summary>
public static class ReminderGate
{
    /// <summary>飞书没配好时给用户的提示（用户指定的原文）。</summary>
    public const string FeishuMissingMessage = "还没有配置飞书，无法触达提醒";

    /// <summary>飞书 webhook 填了没有。</summary>
    public static bool IsFeishuConfigured(AppConfig? config)
        => !string.IsNullOrWhiteSpace(config?.FeishuWebhook);

    /// <summary>
    /// 有没有能把提醒送出去的通道。提醒走 webhook（<see cref="ReminderService"/> 里飞书、企业微信各推一次），
    /// 所以两个都空才叫"送不出去"——只配了企业微信的时候提醒是能到的，不该再报「还没配飞书」。
    /// </summary>
    public static bool HasDeliveryChannel(AppConfig? config)
        => IsFeishuConfigured(config) || !string.IsNullOrWhiteSpace(config?.WeComWebhook);

    /// <summary>
    /// 这次「提醒时间」下拉的切换要不要弹提示：
    /// 从「不提醒」切到一个真的提醒档位、而当前没有任何推送通道时提示一次。
    /// 「到时提醒」（提前量 0）也算真的提醒档位 —— 它到点一样推，没通道一样送不出去。
    ///
    /// 只在"第一次从无到有"时提示：档位之间来回换（提前 3 分钟 → 提前 10 分钟）
    /// 不再重复弹，否则连着点几下会弹出一串同样的话。
    /// </summary>
    public static bool ShouldWarnOnLeadChange(AppConfig? config, string? previousLabel, string? newLabel)
        => !HasDeliveryChannel(config)
           && ReminderLeadCatalog.ToMinutes(newLabel) is not null
           && ReminderLeadCatalog.ToMinutes(previousLabel) is null;
}
