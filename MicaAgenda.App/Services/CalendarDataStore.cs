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

            File.Move(temporaryPath, _path, overwrite: true);
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

    private static void Normalize(CalendarData data)
    {
        if (data.Settings.BackgroundMode == CalendarBackgroundMode.ClearBorder)
        {
            data.Settings.BackgroundMode = CalendarBackgroundMode.None;
        }

        // 完成态与完成时间戳必须自洽，否则报告里会出现"已完成但用时算不出来"的记录。
        // 常见来源：外部 API / MCP 直接把 IsCompleted 置 true 却没写 CompletedAt，
        // 或导入的 JSON 两个字段本身就不一致。
        var fallback = DateTimeOffset.Now;
        foreach (var task in data.Tasks)
        {
            task.Normalize(fallback);
        }
    }
}
