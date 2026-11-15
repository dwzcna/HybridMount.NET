using HybridMount.Commons.Persistence;
using HybridMount.Commons.Protocols;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests.Fixtures.Protocols;
/// <summary>仅测试工程允许创建模拟源；生产工厂拒绝模拟挂载连接。</summary>
internal sealed class TestSourceFactory : IFileSourceFactory
{
    public static TestSourceFactory Instance { get; } = new();
    public async Task<IFileSource> CreateAsync(MountDefinition mount, IMountRepository repository, bool recovery, CancellationToken token)
    {
        var source = new MockFileSource(Path.Combine(repository.DataDirectory,"MockSources",mount.Id.ToString("N")));
        if (!recovery) await source.SeedAsync(token); return source;
    }
}
