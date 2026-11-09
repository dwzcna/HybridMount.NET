using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.WebDAV;

public sealed partial class WebDavFileSource : IMountSourceRuntime, IMountNetworkSource
{
    private MountQueueBinding? _queueBinding;
    MountQueueBinding? IMountNetworkSource.QueueBinding=>_queueBinding;
    void IMountNetworkSource.BindQueue(MountQueueBinding binding)=>_queueBinding=binding;
    string IMountSourceRuntime.RemoteScope(long revision)=>RemoteScope(revision);
    string? IMountSourceRuntime.ConnectionDiagnostics=>CapabilitySnapshot?.ToString();
    event Action? IMountSourceRuntime.CapacityInvalidated {add=>_capacityInvalidated+=value;remove=>_capacityInvalidated-=value;}
    /// <summary>仅真实挂载绑定临时项日志和原作用域观测，配置探测仍没有持久化副作用。</summary>
    void IMountSourceRuntime.AttachRuntime(MountDefinition mount,MountRepository repository)
    {AttachTemporaryJournal(mount,repository);RestoreObservations(repository.RemoteShadow(mount.Id,RemoteScope(mount.CredentialRevision)));}
    Task IMountSourceRuntime.BeforeRegistrationAsync(CancellationToken token)=>RecoverTemporaryItemsAsync(token);
}
