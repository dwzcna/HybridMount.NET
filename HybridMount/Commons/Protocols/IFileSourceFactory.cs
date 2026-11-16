using HybridMount.Commons.Persistence;
using HybridMount.Models.Mounting;
namespace HybridMount.Commons.Protocols;

/// <summary>宿主只依赖工厂；未注册的 SMB/NFS 明确失败，不回退为其他协议。</summary>
public interface IFileSourceFactory
{
    Task<IFileSource> CreateAsync(MountDefinition mount, IMountRepository repository, CancellationToken token);
}
