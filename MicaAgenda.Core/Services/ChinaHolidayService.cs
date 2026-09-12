using System.IO;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

public sealed class ChinaHolidayService
{
    private const string Embedded2026Source = "国务院办公厅关于2026年部分节假日安排的通知";
    private static readonly Uri ApiBaseUri = new("https://timor.tech/api/holiday/year/");

    /// <summary>
    /// 缓存 TTL：缓存距上次成功联网超过此值，强制重拉。
    /// 设为 7 天：太短会浪费 API 配额；太长会漏掉国务院临时调休的更新。
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// 跨年时缓存的"年内不重拉"豁免窗口：同一年内距上次联网小于此值就不重拉（避免重复请求）。
    /// 设为 1 天：保证每天首次联网都会被认为"过期"，避免漏掉临时调整。
    /// </summary>
    public static readonly TimeSpan SameYearRefreshInterval = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _cachePath;
    private readonly HttpClient _httpClient;

    public ChinaHolidayService(string? cachePath = null, HttpClient? httpClient = null)
    {
        _cachePath = cachePath ?? GetDefaultCachePath();
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    public static string GetDefaultCachePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MicaAgenda", "china-holidays.json");
    }

    public async Task<IReadOnlyList<ChinaHoliday>> LoadCachedOrEmbeddedAsync(int year)
    {
        var cache = await LoadCacheAsync();
        return MergeHolidays(GetEmbeddedHolidays(year), cache.Holidays)
            .Where(holiday => holiday.Date.Year == year)
            .OrderBy(holiday => holiday.Date)
            .ToList();
    }

    public async Task<IReadOnlyList<ChinaHoliday>> LoadAndRefreshAsync(int year, CancellationToken cancellationToken = default)
    {
        var cache = await LoadCacheAsync();
        var fallback = MergeHolidays(GetEmbeddedHolidays(year), cache.Holidays)
            .Where(holiday => holiday.Date.Year == year)
            .OrderBy(holiday => holiday.Date)
            .ToList();

        try
        {
            var online = await FetchYearAsync(year, cancellationToken);
            if (online.Count == 0)
            {
                return fallback;
            }

            var updatedYear = MergeHolidays(GetEmbeddedHolidays(year), online)
                .Where(holiday => holiday.Date.Year == year)
                .OrderBy(holiday => holiday.Date)
                .ToList();
            var preservedYears = cache.Holidays.Where(holiday => holiday.Date.Year != year);

            // 记录本次真实联网成功的年份（用字典维护，不重复）
            var onlineYears = new HashSet<int>(cache.OnlineYears ?? new List<int>()) { year };

            await SaveCacheAsync(new ChinaHolidayCache
            {
                Source = online[0].Source,
                UpdatedAt = DateTimeOffset.Now,
                OnlineYears = onlineYears.ToList(),
                Holidays = MergeHolidays(preservedYears, updatedYear)
            });

            return updatedYear;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// 判断当前缓存是否"足够新鲜"，无需重新联网。
    /// 返回 true 表示本次可以直接使用本地缓存；false 表示应该调用 LoadAndRefreshAsync。
    /// </summary>
    public async Task<bool> IsCacheFreshAsync(int year)
    {
        var cache = await LoadCacheAsync();
        if (!cache.OnlineYears.Contains(year))
        {
            // 从未成功联网过该年份 → 必须重拉
            return false;
        }
        var age = DateTimeOffset.Now - cache.UpdatedAt;
        if (age > CacheTtl)
        {
            // 缓存超过 TTL → 强制重拉（避免漏掉临时调休）
            return false;
        }
        // 同一年内距上次联网小于 SameYearRefreshInterval 视为"今天已经拉过"，跳过
        if (age < SameYearRefreshInterval)
        {
            return true;
        }
        // 跨过一天：允许重拉一次
        return false;
    }

    /// <summary>
    /// 给设置页用的"数据状态"快照。
    /// </summary>
    public async Task<HolidayCacheStatus> GetCacheStatusAsync()
    {
        var cache = await LoadCacheAsync();
        var fileInfo = new FileInfo(_cachePath);
        return new HolidayCacheStatus
        {
            Source = cache.Source,
            UpdatedAt = cache.UpdatedAt,
            OnlineYears = new List<int>(cache.OnlineYears),
            HasCacheFile = fileInfo.Exists,
            CacheFileSize = fileInfo.Exists ? fileInfo.Length : 0
        };
    }

    /// <summary>
    /// 强制清空缓存（设置页"清空本地缓存"按钮调用）。
    /// </summary>
    public void ClearCache()
    {
        if (File.Exists(_cachePath))
        {
            File.Delete(_cachePath);
        }
    }

    public async Task<ChinaHolidayCache> LoadCacheAsync()
    {
        if (!File.Exists(_cachePath))
        {
            return new ChinaHolidayCache();
        }

        try
        {
            await using var stream = File.OpenRead(_cachePath);
            return await JsonSerializer.DeserializeAsync<ChinaHolidayCache>(stream, JsonOptions) ?? new ChinaHolidayCache();
        }
        catch (IOException)
        {
            return new ChinaHolidayCache();
        }
        catch (JsonException)
        {
            return new ChinaHolidayCache();
        }
    }

    public async Task SaveCacheAsync(ChinaHolidayCache cache)
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, cache, JsonOptions);
            }

            File.Move(temporaryPath, _cachePath, overwrite: true);
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
                    // 临时文件删不掉不影响缓存主流程
                }
            }
        }
    }

    public static IReadOnlyList<ChinaHoliday> GetEmbeddedHolidays(int year)
    {
        return year == 2026 ? BuildEmbedded2026() : [];
    }

    private async Task<List<ChinaHoliday>> FetchYearAsync(int year, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(new Uri(ApiBaseUri, $"{year}/"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("code", out var codeElement) ||
            codeElement.GetInt32() != 0 ||
            !document.RootElement.TryGetProperty("holiday", out var holidayElement) ||
            holidayElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var source = $"timor.tech/api/holiday/year/{year}";
        var updatedAt = DateTimeOffset.Now;
        var holidays = new List<ChinaHoliday>();

        foreach (var property in holidayElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("date", out var dateElement) ||
                !DateOnly.TryParse(dateElement.GetString(), out var date) ||
                date.Year != year)
            {
                continue;
            }

            var isHoliday = property.Value.TryGetProperty("holiday", out var holidayFlag) && holidayFlag.GetBoolean();
            var name = property.Value.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            holidays.Add(new ChinaHoliday
            {
                Date = date,
                Name = name.Trim(),
                IsHoliday = isHoliday,
                IsMakeupWorkday = !isHoliday,
                Source = source,
                UpdatedAt = updatedAt
            });
        }

        return holidays.OrderBy(holiday => holiday.Date).ToList();
    }

    private static List<ChinaHoliday> MergeHolidays(params IEnumerable<ChinaHoliday>[] sources)
    {
        var byDate = new Dictionary<DateOnly, ChinaHoliday>();
        foreach (var source in sources)
        {
            foreach (var holiday in source)
            {
                byDate[holiday.Date] = holiday;
            }
        }

        return byDate.Values.OrderBy(holiday => holiday.Date).ToList();
    }

    private static List<ChinaHoliday> BuildEmbedded2026()
    {
        var updatedAt = new DateTimeOffset(2025, 11, 4, 0, 0, 0, TimeSpan.FromHours(8));
        return
        [
            Holiday(2026, 1, 1, "元旦", true, updatedAt),
            Holiday(2026, 1, 2, "元旦", true, updatedAt),
            Holiday(2026, 1, 3, "元旦", true, updatedAt),
            Holiday(2026, 1, 4, "元旦后补班", false, updatedAt),
            Holiday(2026, 2, 14, "春节前补班", false, updatedAt),
            Holiday(2026, 2, 15, "春节", true, updatedAt),
            Holiday(2026, 2, 16, "除夕", true, updatedAt),
            Holiday(2026, 2, 17, "初一", true, updatedAt),
            Holiday(2026, 2, 18, "初二", true, updatedAt),
            Holiday(2026, 2, 19, "初三", true, updatedAt),
            Holiday(2026, 2, 20, "初四", true, updatedAt),
            Holiday(2026, 2, 21, "初五", true, updatedAt),
            Holiday(2026, 2, 22, "初六", true, updatedAt),
            Holiday(2026, 2, 23, "初七", true, updatedAt),
            Holiday(2026, 2, 28, "春节后补班", false, updatedAt),
            Holiday(2026, 4, 4, "清明节", true, updatedAt),
            Holiday(2026, 4, 5, "清明节", true, updatedAt),
            Holiday(2026, 4, 6, "清明节", true, updatedAt),
            Holiday(2026, 5, 1, "劳动节", true, updatedAt),
            Holiday(2026, 5, 2, "劳动节", true, updatedAt),
            Holiday(2026, 5, 3, "劳动节", true, updatedAt),
            Holiday(2026, 5, 4, "劳动节", true, updatedAt),
            Holiday(2026, 5, 5, "劳动节", true, updatedAt),
            Holiday(2026, 5, 9, "劳动节后补班", false, updatedAt),
            Holiday(2026, 6, 19, "端午节", true, updatedAt),
            Holiday(2026, 6, 20, "端午节", true, updatedAt),
            Holiday(2026, 6, 21, "端午节", true, updatedAt),
            Holiday(2026, 9, 20, "中秋节前补班", false, updatedAt),
            Holiday(2026, 9, 25, "中秋节", true, updatedAt),
            Holiday(2026, 9, 26, "中秋节", true, updatedAt),
            Holiday(2026, 9, 27, "中秋节", true, updatedAt),
            Holiday(2026, 10, 1, "国庆节", true, updatedAt),
            Holiday(2026, 10, 2, "国庆节", true, updatedAt),
            Holiday(2026, 10, 3, "国庆节", true, updatedAt),
            Holiday(2026, 10, 4, "国庆节", true, updatedAt),
            Holiday(2026, 10, 5, "国庆节", true, updatedAt),
            Holiday(2026, 10, 6, "国庆节", true, updatedAt),
            Holiday(2026, 10, 7, "国庆节", true, updatedAt),
            Holiday(2026, 10, 10, "国庆节后补班", false, updatedAt)
        ];
    }

    private static ChinaHoliday Holiday(int year, int month, int day, string name, bool isHoliday, DateTimeOffset updatedAt)
    {
        return new ChinaHoliday
        {
            Date = new DateOnly(year, month, day),
            Name = name,
            IsHoliday = isHoliday,
            IsMakeupWorkday = !isHoliday,
            Source = Embedded2026Source,
            UpdatedAt = updatedAt
        };
    }
}

/// <summary>
/// 设置页"节假日数据"区显示用的状态快照。
/// </summary>
public sealed class HolidayCacheStatus
{
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<int> OnlineYears { get; set; } = [];
    public bool HasCacheFile { get; set; }
    public long CacheFileSize { get; set; }
}
