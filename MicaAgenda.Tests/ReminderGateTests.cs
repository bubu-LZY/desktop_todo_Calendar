using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.Tests;

public sealed class ReminderGateTests
{
    [Fact]
    public void NoWebhookConfigured_NeverHasDeliveryChannel()
    {
        Assert.False(ReminderGate.HasDeliveryChannel(new AppConfig()));
        Assert.False(ReminderGate.HasDeliveryChannel(new AppConfig { FeishuWebhook = "   " }));
        Assert.False(ReminderGate.HasDeliveryChannel(null));
    }

    [Fact]
    public void EitherWebhookConfigured_IsEnoughToDeliver()
    {
        Assert.True(ReminderGate.HasDeliveryChannel(new AppConfig { FeishuWebhook = "https://open.feishu.cn/x" }));
        Assert.True(ReminderGate.HasDeliveryChannel(new AppConfig { WeComWebhook = "https://qyapi.weixin.qq.com/x" }));
    }

    [Fact]
    public void WarnsOnlyWhenPickingARealLeadWithoutAnyChannel()
    {
        var empty = new AppConfig();
        var configured = new AppConfig { FeishuWebhook = "https://open.feishu.cn/x" };

        // 从「不提醒」切到真的提醒档 + 没通道 → 提示
        Assert.True(ReminderGate.ShouldWarnOnLeadChange(empty, ReminderLeadCatalog.NoneLabel, "提前30分钟"));

        // 「到时提醒」也算真的提醒档：到点一样推，没通道一样送不出去
        Assert.True(ReminderGate.ShouldWarnOnLeadChange(empty, ReminderLeadCatalog.NoneLabel, ReminderLeadCatalog.OnTimeLabel));

        // 通道配好了 → 不提示
        Assert.False(ReminderGate.ShouldWarnOnLeadChange(configured, ReminderLeadCatalog.NoneLabel, "提前30分钟"));

        // 档位之间来回换（已经是提醒档了）→ 不再重复提示
        Assert.False(ReminderGate.ShouldWarnOnLeadChange(empty, "提前10分钟", "提前30分钟"));
        Assert.False(ReminderGate.ShouldWarnOnLeadChange(empty, ReminderLeadCatalog.OnTimeLabel, "提前30分钟"));

        // 选回「不提醒」→ 不提示
        Assert.False(ReminderGate.ShouldWarnOnLeadChange(empty, "提前30分钟", ReminderLeadCatalog.NoneLabel));

        // 只是打开表单（没有旧值、也没选新值）→ 不提示
        Assert.False(ReminderGate.ShouldWarnOnLeadChange(empty, null, null));
    }

    [Fact]
    public void FeishuMissingMessageMatchesTheAgreedWording()
    {
        Assert.Equal("还没有配置飞书，无法触达提醒", ReminderGate.FeishuMissingMessage);
    }
}
