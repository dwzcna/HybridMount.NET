using HybridMount.Services.Mounting;
namespace HybridMount.Common;

/// <summary>冲突线不占网络执行位；用户必须提交当前比较证明，原业务执行器核验后才创建任务或保护本地内容。</summary>
public interface IMountingConflict
{
    string Id {get;}
    Guid MountId {get;}
    string? Path {get;}
    Task<ConflictComparison> CompareAsync(CancellationToken token=default);
    Task<string> ResolveAsync(ConflictComparison proof,bool useLocal,CancellationToken token=default);
}
