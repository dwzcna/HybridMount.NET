namespace HybridMount.Commons.Scheduling;

/// <summary>只在本连接使用的传输绑定；代次不持久化，不进入公共IPC或配置。</summary>
internal sealed record MountQueueBinding(IMountTaskQueue Queue,Guid MountId,long Generation);
