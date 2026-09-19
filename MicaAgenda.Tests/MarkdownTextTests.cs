using MicaAgenda.App.Helpers;

namespace MicaAgenda.Tests;

/// <summary>
/// MarkdownText 的规整行为。这些断言锁的是「气泡里不该再出现原样 Markdown 记号」这条用户诉求，
/// 以及「不能误伤任务标题里的数学符号」这条安全底线。
/// </summary>
public sealed class MarkdownTextTests
{
    [Fact]
    public void BoldMarkers_AreStrippedButTextKept()
    {
        Assert.Equal("重点", MarkdownText.ToPlainText("**重点**"));
        Assert.Equal("今天要 开会", MarkdownText.ToPlainText("今天要 **开会**"));
        Assert.Equal("A B", MarkdownText.ToPlainText("__A__ __B__"));
    }

    [Fact]
    public void SingleEmphasis_IsStrippedWhenPaired()
    {
        Assert.Equal("斜体", MarkdownText.ToPlainText("*斜体*"));
        Assert.Equal("斜体", MarkdownText.ToPlainText("_斜体_"));
        Assert.Equal("代码", MarkdownText.ToPlainText("`代码`"));
    }

    [Fact]
    public void ArithmeticAsterisks_AreNotMangled()
    {
        // 任务标题里 "3*4*5" 的星号两侧都是数字，不是 Markdown 强调，必须原样保留。
        Assert.Equal("计算 3*4*5 的结果", MarkdownText.ToPlainText("计算 3*4*5 的结果"));
    }

    [Fact]
    public void Headings_LoseHashesButKeepText()
    {
        Assert.Equal("标题", MarkdownText.ToPlainText("# 标题"));
        Assert.Equal("二级", MarkdownText.ToPlainText("### 二级"));
    }

    [Fact]
    public void UnorderedList_MarkersBecomeBullets()
    {
        Assert.Equal("• 第一项\n• 第二项", MarkdownText.ToPlainText("- 第一项\n* 第二项"));
    }

    [Fact]
    public void HorizontalRule_IsDroppedEntirely()
    {
        // 分隔线本身消失，但两侧的段落分隔（一个空行）保留 —— 原是三段，去掉线后仍是两段。
        Assert.Equal("上\n\n下", MarkdownText.ToPlainText("上\n\n---\n\n下"));
        // 紧贴文字的分隔线不留任何残留空行
        Assert.Equal("上\n下", MarkdownText.ToPlainText("上\n---\n下"));
    }

    [Fact]
    public void BlockQuote_KeepsTextDropsMarker()
    {
        Assert.Equal("引用内容", MarkdownText.ToPlainText("> 引用内容"));
    }

    [Fact]
    public void InlineLink_KeepsOnlyLabel()
    {
        Assert.Equal("看这里", MarkdownText.ToPlainText("[看这里](https://example.com/x)"));
    }

    [Fact]
    public void CodeFence_ContentIsUntouched()
    {
        var input = "说明：\n```\n**不要动**\n```";
        var output = MarkdownText.ToPlainText(input);

        Assert.Contains("**不要动**", output);
        Assert.DoesNotContain("```", output);
    }

    [Fact]
    public void ConsecutiveBlankLines_AreCollapsed()
    {
        Assert.Equal("A\n\nB", MarkdownText.ToPlainText("A\n\n\n\n\nB"));
    }

    [Fact]
    public void PlainText_PassesThroughUnchanged()
    {
        const string text = "明天下午三点开会，记得带上笔记本。";
        Assert.Equal(text, MarkdownText.ToPlainText(text));
    }

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MarkdownText.ToPlainText(null));
        Assert.Equal(string.Empty, MarkdownText.ToPlainText(string.Empty));
    }

    [Fact]
    public void LoneAsterisk_IsPreserved()
    {
        // 落单的记号不是 Markdown，原样保留，避免吃字
        Assert.Equal("折扣 *", MarkdownText.ToPlainText("折扣 *"));
    }

    [Fact]
    public void RealisticAssistantReply_IsCleaned()
    {
        const string reply =
            "## 本周任务\n" +
            "你有 **3** 项待办：\n" +
            "- 周一：写周报\n" +
            "- 周三：*评审*\n" +
            "\n---\n" +
            "需要我帮你安排时间吗？";

        var output = MarkdownText.ToPlainText(reply);

        Assert.DoesNotContain("##", output);
        Assert.DoesNotContain("**", output);
        Assert.Contains("• 周一：写周报", output);
        Assert.Contains("• 周三：评审", output);
        Assert.Contains("你有 3 项待办：", output);
        Assert.Contains("需要我帮你安排时间吗？", output);
        Assert.DoesNotContain("---", output);
    }
}
