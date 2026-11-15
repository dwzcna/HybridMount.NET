namespace HybridMount.Models.Mounting;

/// <summary>本地路径屏障的纯状态规则；模型索引与持久查询使用同一语义，不访问数据库。</summary>
internal static class RemotePendingRules
{
    internal static bool BlocksLocalPath(RemotePendingChange item) =>
        item.State is not ("Completed" or "Superseded") && !(item.State == "Canceled" && item.MetadataOnly);
}
