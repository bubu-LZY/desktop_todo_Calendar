using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

public sealed class CalendarDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public CalendarDataStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
    }

    public static string GetDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MicaAgenda", "calendar-data.json");
    }

    public async Task<CalendarData> LoadAsync()
    {
        if (!File.Exists(_path))
        {
            return new CalendarData();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var data = await JsonSerializer.DeserializeAsync<CalendarData>(stream, JsonOptions) ?? new CalendarData();
            Normalize(data);
            return data;
        }
        catch (IOException)
        {
            return new CalendarData();
        }
        catch (JsonException)
        {
            return new CalendarData();
        }
    }

    public async Task SaveAsync(CalendarData data)
    {
        // 落盘前统一自洽：新建/外部写入的任务可能没带 UpdatedAt，
        // 补齐后同步仲裁才有可靠的时间戳可比较。
        Normalize(data);
        await _saveGate.WaitAsync();
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
            }

            MoveWithRetry(temporaryPath, _path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // 临时文件删不掉不影响下一次保存
                }
            }

            _saveGate.Release();
        }
    }

    /// <summary>
    /// 原子替换目标文件，遇到瞬时文件锁（杀毒 / 索引器 / 并发读短暂占用）时退避重试。
    /// Windows 的 File.Move(overwrite:true) 底层是 MoveFileEx，目标被占用会抛
    /// UnauthorizedAccessException/IOException；不重试会让保存（含自动保存）偶发失败。
    /// </summary>
    private static void MoveWithRetry(string source, string destination)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private static void Normalize(CalendarData data)
    {
        // 历史背景主题统一迁到现役主题（ClearBorder → 无背景；已下线的毛玻璃/透明/纯色 → 白雾玻璃）。
        // 放在加载路径上，老配置文件一进来就被改正，UI 里不会出现「下拉列表选不中的值」。
        data.Settings.BackgroundMode = data.Settings.BackgroundMode.MigrateLegacy();

        // 完成态与完成时间戳必须自洽，否则报告里会出现"已完成但用时算不出来"的记录。
        // 常见来源：外部 API / MCP 直接把 IsCompleted 置 true 却没写 CompletedAt，
        // 或导入的 JSON 两个字段本身就不一致。
        var fallback = DateTimeOffset.Now;
        foreach (var task in data.Tasks)
        {
            task.Normalize(fallback);
        }

        NormalizePendingReviewDeletions(data);
    }

    /// <summary>
    /// 待删记录（用户在日历里删掉的复习任务，等着通知对端一起删）的自洽化：
    /// 丢掉日期/标题缺失的脏数据、按「日期 + 标题」去重、清掉过老的记录。
    ///
    /// 去重是必须的：配对键就是「日期 + 标题」，重复记录会让同一条被反复推送；
    /// 过期清理是兜底：对端长期不可达时记录只增不减，会把数据文件撑大。
    /// </summary>
    private static void NormalizePendingReviewDeletions(CalendarData data)
    {
        if (data.PendingReviewDeletions.Count == 0)
        {
            return;
        }

        var cutoff = DateTimeOffset.Now.AddDays(-90);
        data.PendingReviewDeletions = data.PendingReviewDeletions
            .Where(d => d.Date != default
                        && !string.IsNullOrWhiteSpace(d.Title)
                        && d.DeletedAt >= cutoff)
            .GroupBy(d => ReviewSyncPlanner.KeyOf(d.Date, d.Title), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(d => d.DeletedAt).First())
            .ToList();
    }
}
