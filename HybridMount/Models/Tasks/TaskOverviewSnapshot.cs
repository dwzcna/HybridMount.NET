using HybridMount.Models.Mounting;
namespace HybridMount.Models.Tasks;

/// <summary>首页不可变概览。轮次只属于当前进程；进度只表示结束比例，不能据此推断全部成功。</summary>
public sealed record TaskOverviewSnapshot(bool Initialized, long Round, int Total, int Ended, int Succeeded,
    int Failed, int Canceled, int Skipped, int RunningCount, IReadOnlyList<MountOperationSnapshot> RunningTasks,
    string? Error = null)
{
    public static TaskOverviewSnapshot Empty { get; } = new(false, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<MountOperationSnapshot>());
    public bool HasRound => Total > 0;
    public bool IsClosed => HasRound && Ended == Total;
    public int Unfinished => Total - Ended;
    public int AdditionalRunning => Math.Max(0, RunningCount - RunningTasks.Count);
    public double Percent => Total == 0 ? 0 : (double)Ended * 100 / Total;
}
