namespace HybridMount.Models.Mounting;

/// <summary>任务展示历史的清理范围，不代表取消、回滚或删除同步数据。</summary>
public enum TaskHistoryClearMode { Completed,Failed,All }
/// <summary>显式终态白名单；未知状态、冲突、等待及结果待核对始终保留。</summary>
public static class TaskHistoryRules
{
    private static readonly IReadOnlyList<string> Completed=Array.AsReadOnly(new[]{"Completed"});
    private static readonly IReadOnlyList<string> Failed=Array.AsReadOnly(new[]{"Failed","PartialFailure"});
    private static readonly IReadOnlyList<string> All=Array.AsReadOnly(new[]{"Completed","Failed","PartialFailure","Canceled","Interrupted","Ignored","Superseded"});
    /// <summary>供界面和持久化共用，不接收来自外部配置的状态或SQL。</summary>
    public static IReadOnlyList<string> States(TaskHistoryClearMode mode)=>mode switch
    {TaskHistoryClearMode.Completed=>Completed,TaskHistoryClearMode.Failed=>Failed,TaskHistoryClearMode.All=>All,_=>throw new ArgumentOutOfRangeException(nameof(mode))};
    public static bool Matches(TaskHistoryClearMode mode,string state)=>States(mode).Contains(state);
    public static bool IsHistory(string state)=>All.Contains(state);
}
