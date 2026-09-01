using System.Security.Cryptography;
using System.Text;

namespace MicaAgenda.App.Services;

/// <summary>
/// 两个本地 HTTP 服务共用的鉴权工具。
/// 单独抽出避免 McpServer / TaskApiServer 各自复制实现时漏改一处。
/// </summary>
public static class AuthUtil
{
    /// <summary>
    /// 常时间比较两个 token —— 防止用响应时间反推 Token 的侧信道攻击。
    /// 实际威胁不大（攻击者本来就只能从本机发请求），但写起来不费事，留着不亏。
    /// </summary>
    public static bool TokensEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length
               && CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}