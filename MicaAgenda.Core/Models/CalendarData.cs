namespace MicaAgenda.App.Models;

public sealed class CalendarData
{
    public CalendarSettings Settings { get; set; } = new();
    public List<CalendarTask> Tasks { get; set; } = [];

    /// <summary>
    /// 已在日历里删除、但还没被对端确认的复习任务（见 <see cref="ReviewDeletion"/>）。
    /// 存在于这份清单里的复习任务不会被同步重建（否则删除会被「复活」），
    /// 直到对端确认删除后由同步服务清掉。
    /// </summary>
    public List<ReviewDeletion> PendingReviewDeletions { get; set; } = [];
}
