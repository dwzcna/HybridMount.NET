using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class WebDavFileSource : IMountSourceRuntime
{
    string IMountSourceRuntime.RemoteScope(long revision)=>RemoteScope(revision);
    /// <summary>仅真实挂载绑定临时项日志和原作用域观测，配置探测仍没有持久化副作用。</summary>
    void IMountSourceRuntime.AttachRuntime(MountDefinition mount,MountRepository repository)
    {AttachTemporaryJournal(mount,repository);RestoreObservations(repository.RemoteShadow(mount.Id,RemoteScope(mount.CredentialRevision)));}
    Task IMountSourceRuntime.BeforeRegistrationAsync(CancellationToken token)=>RecoverTemporaryItemsAsync(token);
}
