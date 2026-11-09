namespace HybridMount.Common;

/// <summary>SQS、定时刷新或服务器观察的独立生命周期；监听不占存储网络名额，产生的业务请求重新进入任务线。</summary>
public interface IMountingObserver
{
    string Id {get;}
    Guid? MountId {get;}
    Task ObserveAsync(CancellationToken token);
}
