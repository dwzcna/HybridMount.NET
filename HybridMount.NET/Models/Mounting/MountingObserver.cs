namespace HybridMount.Models.Mounting;

/// <summary>观察器闭包保留原有持久提交及确认规则；取消只影响当前监听所有者。</summary>
internal sealed record MountingObserver(string Id,Guid? MountId,Func<CancellationToken,Task> Observe):IMountingObserver
{
    public Task ObserveAsync(CancellationToken token)=>Observe(token);
}
