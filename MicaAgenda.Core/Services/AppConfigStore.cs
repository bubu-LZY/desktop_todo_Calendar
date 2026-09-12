using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 读写应用级配置文件 app-config.json。
/// </summary>
public sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public AppConfigStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
    }

    public static string GetDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MicaAgenda", "app-config.json");
    }

    public async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(_path))
        {
            return new AppConfig();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions) ?? new AppConfig();
        }
        catch (IOException)
        {
            return new AppConfig();
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
    }

    /// <summary>同步读取配置（用于窗口初始化前）。失败时返回默认配置。</summary>
    public AppConfig Load()
    {
        if (!File.Exists(_path))
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (IOException)
        {
            return new AppConfig();
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        // 原子写入：先写到临时文件再 rename。直接 File.Create 覆盖 + 中途崩溃
        // 会留下截断的 JSON，下次启动 LoadAsync 只能返回默认配置，token / webhook
        // 全部静默丢失，用户毫无感知。CalendarDataStore 用的是同款写法。
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
                await JsonSerializer.SerializeAsync(stream, config, JsonOptions);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { /* 临时文件删不掉不影响 */ }
            }

            _saveGate.Release();
        }
    }
}
