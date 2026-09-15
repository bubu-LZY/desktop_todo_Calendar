namespace MicaAgenda.App.Helpers;

/// <summary>
/// 字节数的中文短串格式化（"52.8 MB" / "860 KB"）。
/// 更新包大小、下载进度文案都要用，放在 Helpers 里两处共用，避免各写一份、慢慢跑偏。
/// </summary>
public static class ByteText
{
    /// <summary>把字节数格式化成人类可读的短串；0 或负数返回「未知大小」。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "未知大小";
        }

        var mb = bytes / 1024d / 1024d;
        return mb >= 1 ? $"{mb:0.#} MB" : $"{bytes / 1024d:0} KB";
    }
}
