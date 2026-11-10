namespace HybridMount.Models.Mounting.Transactions;

/// <summary>保留原内部测试入口；不在通用引擎中引入S3事件类型。</summary>
internal sealed partial class RemoteSyncEngine
{
    public Task ObserveS3HintsAsync(CancellationToken token)=>ObserveEventHintsAsync(token);
}
