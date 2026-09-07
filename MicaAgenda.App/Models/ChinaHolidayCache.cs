namespace MicaAgenda.App.Models;

public sealed class ChinaHolidayCache
{
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 本次已成功联网的年份。空数组表示尚未联网过（缓存可能来自更早的版本或首次启动）。
    /// 设置页用这个字段判断"上次是真实在线拉取 vs 仅本地兜底"。
    /// </summary>
    public List<int> OnlineYears { get; set; } = [];

    public List<ChinaHoliday> Holidays { get; set; } = [];
}
