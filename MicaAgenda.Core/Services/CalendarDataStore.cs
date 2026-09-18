using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;

namespace MicaAgenda.App.Services;

/// <summary>
/// 数据文件读取失败时抛出，用于和「首次启动（文件不存在）」严格区分。
/// 宿主拿到它必须：① 提示用户；② 关闭自动保存，避免空数据把原文件覆盖回去。
/// </summary>
public sealed class CalendarDataLoadException : Exception
{
    public CalendarDataLoadException(string message, Exception inner) : base(message, inner)
    {
    }
}

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

            // 加载成功后，把「本次会话开始前」的数据拍一份快照，供误删 / 逻辑性覆盖后回退。
            // （parse 失败的数据不在这里，走下面的 quarantine 隔离，不会被快照顶掉。）
            WriteStartupSnapshot();
            return data;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 数据文件读不出来（损坏 / 被杀毒锁住 / 上次写入被断电截断）。
            //
            // 绝不能静默返回空对象：宿主拿不到失败信号，会照常建 ViewModel，
            // 用户随手拖一下窗口就触发自动保存，把空数据原子覆盖回原文件 —— 不可逆丢失。
            // 这里先把坏文件改名隔离（绝不留在原位被覆盖），再向上抛，让宿主报警并禁用自动保存。
            QuarantineCorruptFile(ex);
            throw new CalendarDataLoadException(
                $"数据文件无法读取，已隔离保存（{_path}）。请从启动快照或备份目录恢复。", ex);
        }
    }

    public async Task SaveAsync(CalendarData data)
    {
        await _saveGate.WaitAsync();
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            // 落盘前统一自洽：新建/外部写入的任务可能没带 UpdatedAt，补齐后同步仲裁才有可靠的时间戳。
            //
            // 必须在锁内：Normalize 会原地改 data.Settings、给每个 task 补 UpdatedAt、甚至重 assign
            // 重复 Id。放到锁外的话，API/MCP 写入、UI 编辑、定时保存三者并发时，Normalize 会和
            // 遍历 data.Tasks 的代码同时跑，出现「Collection was modified」或 Id 在遍历途中被改写的偶发错乱。
            Normalize(data);

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

            await MoveWithRetryAsync(temporaryPath, _path);
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
    /// 退避用 await Task.Delay 而不是 Thread.Sleep：SaveAsync 常从 UI 线程同步上下文发起，
    /// Thread.Sleep 会冻结界面（最坏约 100+200+300+400+500+600=2.1s），纯属无谓卡顿。
    /// </summary>
    private static async Task MoveWithRetryAsync(string source, string destination)
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
                await Task.Delay(20 * attempt);
            }
        }
    }

    /// <summary>隔离坏数据文件：改名留存，绝不留在原位被下一次自动保存覆盖。</summary>
    private void QuarantineCorruptFile(Exception ex)
    {
        try
        {
            var quarantine = $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_path, quarantine, overwrite: false);
            AppLog.Error(ex, $"CalendarDataStore.Load：数据文件损坏，已隔离到 {quarantine}");
        }
        catch (Exception moveEx)
        {
            // 隔离失败（文件被独占锁死等）：原文件仍留在原位，但我们已经向上抛错，
            // 宿主会关闭自动保存，至少不会再被覆盖。
            AppLog.Error(moveEx, "CalendarDataStore.Quarantine：隔离失败，原文件仍留在原位");
        }
    }

    /// <summary>
    /// 每次启动加载成功后，把「本次会话开始前」的数据拍一份滚动快照
    /// （calendar-data.json.bak / .bak.2 / .bak.3，共 3 份）。
    ///
    /// 这是针对「逻辑性数据丢失」的兜底：如果某个 bug 在今天清空了任务后又正常自动保存，
    /// 用户至少还有昨天 / 前天的快照可以手工回退 —— 因为那种情况文件能正常解析，
    /// quarantine 和 parse 异常都救不了它。
    /// </summary>
    private void WriteStartupSnapshot()
    {
        try
        {
            const int keep = 3;
            var newest = _path + ".bak";

            // 轮转：.bak.3 扔掉，其余各往后挪一位，再把当前文件复制成新的 .bak。
            var oldest = newest + "." + keep;
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = keep - 1; i >= 1; i--)
            {
                var from = newest + "." + i;
                var to = newest + "." + (i + 1);
                if (File.Exists(from))
                {
                    File.Move(from, to, overwrite: true);
                }
            }

            if (File.Exists(newest))
            {
                File.Move(newest, newest + ".1", overwrite: true);
            }

            File.Copy(_path, newest, overwrite: true);
        }
        catch
        {
            // 快照失败不影响主流程：最坏就是没有回退点，加载照常返回。
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

        NormalizeTaskIds(data);

        NormalizePendingReviewDeletions(data);
    }

    /// <summary>
    /// 任务 Id 必须唯一：整个 UI 都按 Id 复用 ViewModel 实例（日期格子的增量刷新、
    /// 右侧面板的实例池）。两条任务共用一个 Id 时，池子会把同一个实例发两次，
    /// 增量插入随即吃掉其中一条 —— 表现出来就是"日期格子里比右侧面板少显示一条"。
    /// 同步 / 导入 / 手工改过的 JSON 都可能产生重复 Id，所以在这里一次性改正。
    /// </summary>
    private static void NormalizeTaskIds(CalendarData data)
    {
        if (data.Tasks.Count == 0)
        {
            return;
        }

        var seen = new HashSet<Guid>();
        foreach (var task in data.Tasks)
        {
            if (task.Id == Guid.Empty || !seen.Add(task.Id))
            {
                task.Id = Guid.NewGuid();
                seen.Add(task.Id);
            }
        }
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
