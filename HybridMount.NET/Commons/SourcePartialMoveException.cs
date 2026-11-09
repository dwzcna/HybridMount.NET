using HybridMount.Models.Mounting;
namespace HybridMount.Commons;

/// <summary>目录/文件移动不是事务；成功的目标和仍保留的源通过逐项结果交付调用者。</summary>
public sealed class SourcePartialMoveException(IReadOnlyList<MountItemResult> results,string protocol="S3") : IOException(protocol+" 移动部分完成，请核对源和目标。")
{
    public IReadOnlyList<MountItemResult> Results { get; } = results;
}
