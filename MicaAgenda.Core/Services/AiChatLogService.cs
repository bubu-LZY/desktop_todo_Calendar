using System.IO;
using System.Text;
using System.Text.Json;

namespace MicaAgenda.App.Services;

/// <summary>一条 AI 对话日志（谁说的、什么时候、内容）。</summary>
public sealed record AiChatLogEntry(DateTime Time, string Role, string Content);

/// <summary>
/// AI 对话日志：按天写一个 JSONL 文件（%APPDATA%\MicaAgenda\ai-logs\ai-chat-yyyyMMdd.jsonl），
/// 只保留最近 <see cref="RetainDays"/> 天，过期文件在每次写入时自动删除 ——
/// 等效于「每周固定清理一次」，但滚动删除不挑时间点、绝不会攒出一堆旧文件。
/// 记录失败永远不影响对话主流程（静默吞掉）。
/// </summary>
public static class AiChatLogService
{
    /// <summary>日志保留天数：只保留最近 7 天（即"本周"），更早的自动删除。</summary>
    public const int RetainDays = 7;

    private static readonly object Gate = new();

    public static string LogDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "MicaAgenda", "ai-logs");
        }
    }

    /// <summary>追加一条日志。任何异常（磁盘/权限/序列化）都静默吞掉。</summary>
    public static void Append(string role, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var directory = LogDirectory;
                Directory.CreateDirectory(directory);
                Cleanup(directory);

                var entry = new AiChatLogEntry(DateTime.Now, role, content);
                var line = JsonSerializer.Serialize(entry);
                var file = Path.Combine(directory, $"ai-chat-{DateTime.Now:yyyyMMdd}.jsonl");
                File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志写不进去不影响对话主流程
        }
    }

    /// <summary>读取保留期内的全部日志（按时间升序），返回可直接展示的多行文本。</summary>
    public static string ReadAll()
    {
        try
        {
            lock (Gate)
            {
                var directory = LogDirectory;
                if (!Directory.Exists(directory))
                {
                    return string.Empty;
                }

                var lines = new List<(DateTime Time, string Text)>();
                foreach (var file in Directory.GetFiles(directory, "ai-chat-*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        try
                        {
                            var entry = JsonSerializer.Deserialize<AiChatLogEntry>(line);
                            if (entry is not null)
                            {
                                lines.Add((entry.Time, Format(entry)));
                            }
                        }
                        catch
                        {
                            // 单行损坏跳过，不影响其余日志
                        }
                    }
                }

                return string.Join(
                    Environment.NewLine,
                    lines.OrderBy(item => item.Time).Select(item => item.Text));
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Format(AiChatLogEntry entry)
    {
        var who = entry.Role switch
        {
            "user" => "我",
            "assistant" => "AI",
            var other => other
        };

        return $"[{entry.Time:MM-dd HH:mm}] {who}：{entry.Content.Replace("\r", " ").Replace("\n", " ")}";
    }

    /// <summary>删除保留期之外的日志文件（文件名里带日期，解析不出的按最后写入时间兜底判断）。</summary>
    private static void Cleanup(string directory)
    {
        var today = DateTime.Today;
        var oldestKeep = today.AddDays(-(RetainDays - 1));

        foreach (var file in Directory.GetFiles(directory, "ai-chat-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var datePart = name.Length >= 8 ? name[^8..] : string.Empty;

            bool expired;
            if (DateOnly.TryParseExact(datePart, "yyyyMMdd", out var day))
            {
                expired = day < DateOnly.FromDateTime(oldestKeep);
            }
            else
            {
                expired = File.GetLastWriteTime(file).Date < oldestKeep;
            }

            if (expired)
            {
                File.Delete(file);
            }
        }
    }
}
