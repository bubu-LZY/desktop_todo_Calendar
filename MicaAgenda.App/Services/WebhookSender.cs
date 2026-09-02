using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MicaAgenda.App.Services;

/// <summary>
/// 飞书 / 企业微信群机器人 webhook 的发送封装。
/// 提醒、备份、定时报告三条链路共用同一份实现，避免各处重复拼 payload。
/// </summary>
public sealed class WebhookSender : IDisposable
{
    private readonly HttpClient _httpClient;

    public WebhookSender(TimeSpan? timeout = null)
    {
        _httpClient = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
    }

    /// <summary>发送飞书 text 消息。</summary>
    public Task SendFeishuTextAsync(string webhook, string text)
    {
        // 飞书自定义机器人 text 消息上限约 20KB，超了整个请求会被拒
        var payload = new { msg_type = "text", content = new { text = Truncate(text, 18000) } };
        return PostJsonAsync(webhook, payload);
    }

    /// <summary>发送企业微信 text 消息。</summary>
    public Task SendWeComTextAsync(string webhook, string text)
    {
        // 企微 text 消息 content 上限 2048 字节，按 UTF-8 字节数截断
        var payload = new { msgtype = "text", text = new { content = TruncateByBytes(text, 1800) } };
        return PostJsonAsync(webhook, payload);
    }

    /// <summary>发送企业微信 markdown 消息（比纯文本在企微里排版更好）。</summary>
    public Task SendWeComMarkdownAsync(string webhook, string markdown)
    {
        var payload = new { msgtype = "markdown", markdown = new { content = TruncateByBytes(markdown, 3800) } };
        return PostJsonAsync(webhook, payload);
    }

    /// <summary>
    /// 发送自定义 webhook。自动识别飞书地址并使用卡片格式，
    /// 其他地址继续按企业微信 text 格式发送，兼容 Server 酱 / PushPlus 等中转服务。
    /// </summary>
    public Task SendCustomTextAsync(string webhook, string text, string markdown)
    {
        if (IsFeishuWebhook(webhook))
        {
            return SendFeishuCardAsync(webhook, "任务完成情况", markdown);
        }

        return SendWeComTextAsync(webhook, text);
    }

    /// <summary>发送飞书交互式卡片消息。</summary>
    public Task SendFeishuCardAsync(string webhook, string title, string markdown)
    {
        var payload = new
        {
            msg_type = "interactive",
            card = new
            {
                config = new { wide_screen_mode = true },
                header = new
                {
                    title = new { tag = "plain_text", content = Truncate(title, 40) },
                    template = "blue"
                },
                elements = new object[]
                {
                    new
                    {
                        tag = "div",
                        text = new
                        {
                            tag = "lark_md",
                            content = Truncate(markdown, 18000)
                        }
                    }
                }
            }
        };

        return PostJsonAsync(webhook, payload);
    }

    private async Task PostJsonAsync(string url, object payload)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("webhook 地址为空", nameof(url));
        }

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Trim(body, 300)}");
        }

        // webhook 即使返回 200，业务失败也会体现在 errcode 上（常见于地址写错 / 机器人被移除）
        var errText = TryReadError(body);
        if (errText is not null)
        {
            throw new InvalidOperationException(errText);
        }
    }

    /// <summary>解析飞书/企微返回体里的 errcode；成功（0）或解析不出时返回 null。</summary>
    private static string? TryReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 飞书返回 { code, msg } 或 { StatusCode, StatusMessage, code, msg }
            if (root.TryGetProperty("code", out var feishuCode))
            {
                if (feishuCode.ValueKind == JsonValueKind.Number && feishuCode.GetInt32() != 0)
                {
                    var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
                    return $"飞书返回错误 code={feishuCode.GetInt32()} {msg}";
                }

                // 飞书偶发用字符串 code
                if (feishuCode.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(feishuCode.GetString()) &&
                    feishuCode.GetString() != "0")
                {
                    var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
                    return $"飞书返回错误 {feishuCode.GetString()} {msg}";
                }

                return null;
            }

            // 企微返回 { errcode, errmsg }
            if (root.TryGetProperty("errcode", out var wecomCode) &&
                wecomCode.ValueKind == JsonValueKind.Number &&
                wecomCode.GetInt32() != 0)
            {
                var msg = root.TryGetProperty("errmsg", out var m) ? m.GetString() : null;
                return $"企微返回错误 errcode={wecomCode.GetInt32()} {msg}";
            }
        }
        catch (JsonException)
        {
            // 返回体不是 JSON（例如网关错误页）——状态码已成功，就不再据此报错
            return null;
        }

        return null;
    }

    public static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "\n…（内容过长已截断）";
    }

    /// <summary>按 UTF-8 字节数截断，且不切断多字节字符。</summary>
    public static string TruncateByBytes(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var sb = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var size = Encoding.UTF8.GetByteCount(rune.ToString());
            if (used + size > maxBytes)
            {
                break;
            }

            sb.Append(rune);
            used += size;
        }

        sb.Append("\n…（内容过长已截断）");
        return sb.ToString();
    }

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    private static bool IsFeishuWebhook(string webhook)
    {
        if (!Uri.TryCreate(webhook, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.EndsWith("feishu.cn", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith("larksuite.com", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _httpClient.Dispose();
}
