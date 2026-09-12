using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 自动备份：定时导出全年任务为 JSON，保存到本地并推送飞书/企业微信。
/// 同时提供 JSON 的导入/导出能力。
/// </summary>
public sealed class BackupService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly CalendarData _data;
    private readonly object _syncRoot;
    private readonly Func<AppConfig> _configProvider;
    private readonly System.Threading.Timer _timer;
    private readonly HttpClient _httpClient;
    private readonly object _stateLock = new();
    private DateOnly _lastBackupDate;
    private int _backing;

    public BackupService(CalendarData data, object syncRoot, Func<AppConfig> configProvider)
    {
        _data = data;
        _syncRoot = syncRoot;
        _configProvider = configProvider;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _lastBackupDate = LoadLastBackupDate();
        // 立即执行一次（处理开机补备份），随后每 30 秒检查一次
        _timer = new System.Threading.Timer(_ => CheckAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    /// <summary>每个周期最多保留的备份文件数，超出后从最旧开始删。</summary>
    private const int MaxBackupFiles = 30;

    /// <summary>获取本地备份目录。</summary>
    public static string GetBackupDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MicaAgenda", "backups");
    }

    private static void RotateBackups(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            var files = new DirectoryInfo(dir)
                .GetFiles("micaagenda-backup-*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (var i = MaxBackupFiles; i < files.Count; i++)
            {
                try
                {
                    files[i].Delete();
                }
                catch
                {
                    // 单个删不掉不能影响其它文件
                }
            }
        }
        catch
        {
            // 轮转是"卫生"，不是核心流程；失败了还有最坏情况
        }
    }

    private async void CheckAsync()
    {
        // 备份耗时可能超过 30 秒的定时器周期，用原子标记防止重入重复备份
        if (Interlocked.Exchange(ref _backing, 1) == 1)
        {
            return;
        }

        try
        {
            var config = _configProvider();
            if (!config.BackupEnabled)
            {
                return;
            }

            if (!TimeOnly.TryParse(config.BackupTime, out var backupTime))
            {
                return;
            }

            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            var currentTime = TimeOnly.FromDateTime(now);

            // 今天已备份过，跳过
            if (GetLastBackupDate() == today)
            {
                return;
            }

            // 还没到备份时间，等待
            if (currentTime < backupTime)
            {
                return;
            }

            // 已到（或已过）备份时间：执行备份。
            // 只有本地保存或任一推送渠道成功时才记录“今天已备份”，否则下个周期继续重试。
            var result = await RunBackupAsync();
            var hasLocal = !string.IsNullOrWhiteSpace(result.LocalPath);
            var hasPush = result.FeishuSent || result.WeComSent;
            if (hasLocal || hasPush)
            {
                SetLastBackupDate(today);
            }
        }
        catch (Exception ex)
        {
            // 定时器回调是 async void（线程池），异常会终止进程，必须在此兜底
            AppLog.Error(ex, "BackupService");
        }
        finally
        {
            Interlocked.Exchange(ref _backing, 0);
        }
    }

    private DateOnly GetLastBackupDate()
    {
        lock (_stateLock)
        {
            return _lastBackupDate;
        }
    }

    private void SetLastBackupDate(DateOnly date)
    {
        lock (_stateLock)
        {
            _lastBackupDate = date;
        }

        SaveLastBackupDate(date);
    }

    private static string GetStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MicaAgenda", "backup-state.json");
    }

    private static DateOnly LoadLastBackupDate()
    {
        try
        {
            var path = GetStatePath();
            if (!File.Exists(path))
            {
                return DateOnly.MinValue;
            }

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("lastBackupDate", out var el) &&
                DateOnly.TryParse(el.GetString(), out var date))
            {
                return date;
            }
        }
        catch
        {
            // 忽略读取失败
        }

        return DateOnly.MinValue;
    }

    private static void SaveLastBackupDate(DateOnly date)
    {
        try
        {
            var path = GetStatePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(new { lastBackupDate = date.ToString("yyyy-MM-dd") });
            File.WriteAllText(path, json);
        }
        catch
        {
            // 忽略写入失败
        }
    }

    /// <summary>立即执行一次备份：导出全年任务 JSON 并推送到飞书/企微。</summary>
    public async Task<BackupResult> RunBackupAsync()
    {
        var config = _configProvider();
        var result = new BackupResult();

        // 1. 导出 JSON
        string json;
        var year = DateTime.Now.Year;
        lock (_syncRoot)
        {
            json = BuildExportJson(year);
        }

        // 2. 保存到本地文件
        try
        {
            var dir = GetBackupDirectory();
            Directory.CreateDirectory(dir);
            var fileName = $"micaagenda-backup-{year}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var filePath = Path.Combine(dir, fileName);
            await File.WriteAllTextAsync(filePath, json, new UTF8Encoding(false));
            result.LocalPath = filePath;

            // 写完顺手轮转：每天一份没限制，一年下来 365 个 ~1MB 文件就是 300MB+，
            // 默认保留最近 30 份够用户回看历史又不会把磁盘撑爆。
            RotateBackups(dir);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"本地保存失败: {ex.Message}");
        }

        // 3. 推送飞书（JSON 文本）- 仅当用户勾选"备份后发送到飞书"
        if (config.BackupSendToFeishu && !string.IsNullOrWhiteSpace(config.FeishuWebhook))
        {
            try
            {
                await SendFeishuTextAsync(config.FeishuWebhook, json);
                result.FeishuSent = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"飞书推送失败: {ex.Message}");
            }
        }

        // 4. 推送企微（JSON 文件）- 仅当用户勾选"备份后发送到企业微信"
        if (config.BackupSendToWeCom && !string.IsNullOrWhiteSpace(config.WeComWebhook))
        {
            try
            {
                await SendWeComFileAsync(config.WeComWebhook, json);
                result.WeComSent = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"企微推送失败: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>导出全年任务为 JSON 字符串。</summary>
    public string ExportYearJson(int year)
    {
        lock (_syncRoot)
        {
            return BuildExportJson(year);
        }
    }

    /// <summary>从 JSON 导入任务（按 Id 合并，已存在的跳过）。返回新增数量。</summary>
    public int ImportJson(string json)
    {
        var imported = JsonSerializer.Deserialize<BackupFile>(json, JsonOptions);
        if (imported?.Tasks is null || imported.Tasks.Count == 0)
        {
            return 0;
        }

        int added = 0;
        lock (_syncRoot)
        {
            var existingIds = _data.Tasks.Select(t => t.Id).ToHashSet();
            foreach (var task in imported.Tasks.OfType<CalendarTask>())
            {
                if (string.IsNullOrWhiteSpace(task.Title))
                {
                    continue;
                }

                task.Title = task.Title.Trim();
                if (existingIds.Contains(task.Id))
                {
                    continue;
                }

                _data.Tasks.Add(task);
                existingIds.Add(task.Id);
                added++;
            }
        }

        return added;
    }

    private string BuildExportJson(int year)
    {
        var tasks = _data.Tasks
            .Where(t => t.Date.Year == year)
            .OrderBy(t => t.Date)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var file = new BackupFile
        {
            App = "MicaAgenda",
            Version = 1,
            ExportedAt = DateTimeOffset.Now,
            Year = year,
            Tasks = tasks
        };

        return JsonSerializer.Serialize(file, JsonOptions);
    }

    private async Task SendFeishuTextAsync(string webhook, string json)
    {
        // 飞书 text 消息有长度限制（约 20KB），超长则截断
        var text = json;
        if (text.Length > 18000)
        {
            text = text[..18000] + "\n...（内容过长已截断，完整文件请从企微渠道或本地备份获取）";
        }

        var payload = new { msg_type = "text", content = new { text } };
        await PostJsonAsync(webhook, payload);
    }

    private async Task SendWeComFileAsync(string webhook, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var fileName = $"micaagenda-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json";

        // 1. 上传文件获取 media_id
        var uploadUrl = BuildWeComUploadUrl(webhook);
        string mediaId;
        using (var form = new MultipartFormDataContent())
        {
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(fileContent, "media", fileName);

            using var response = await _httpClient.PostAsync(uploadUrl, form);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errcode", out var code) && code.GetInt32() != 0)
            {
                throw new InvalidOperationException($"上传失败: {body}");
            }

            mediaId = doc.RootElement.GetProperty("media_id").GetString()
                ?? throw new InvalidOperationException("未返回 media_id");
        }

        // 2. 发送文件消息
        var sendUrl = BuildWeComSendUrl(webhook);
        var payload = new { msgtype = "file", file = new { media_id = mediaId } };
        using var sendResponse = await _httpClient.PostAsync(
            sendUrl,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        var sendBody = await sendResponse.Content.ReadAsStringAsync();
        using var sendDoc = JsonDocument.Parse(sendBody);
        if (sendDoc.RootElement.TryGetProperty("errcode", out var sendCode) && sendCode.GetInt32() != 0)
        {
            throw new InvalidOperationException($"发送失败: {sendBody}");
        }
    }

    private static string BuildWeComUploadUrl(string webhook)
    {
        // 从 https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx 转为 upload_media
        return webhook.Replace("/send?", "/upload_media?").Replace("/send", "/upload_media") + "&type=file";
    }

    private static string BuildWeComSendUrl(string webhook)
    {
        return webhook;
    }

    private async Task PostJsonAsync(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _httpClient.Dispose();
    }

    /// <summary>导出文件的结构。</summary>
    public sealed class BackupFile
    {
        public string App { get; set; } = "MicaAgenda";
        public int Version { get; set; } = 1;
        public DateTimeOffset ExportedAt { get; set; }
        public int Year { get; set; }
        public List<CalendarTask> Tasks { get; set; } = [];
    }
}

/// <summary>备份执行结果。</summary>
public sealed class BackupResult
{
    public bool FeishuSent { get; set; }
    public bool WeComSent { get; set; }
    public string? LocalPath { get; set; }
    public List<string> Errors { get; } = [];
}
