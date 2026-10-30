using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>宿主只依赖工厂；未注册的 SMB/NFS 明确失败，不回退为其他协议。</summary>
public interface IFileSourceFactory
{
    Task<IFileSource> CreateAsync(MountDefinition mount, MountRepository repository, bool recovery, CancellationToken token);
}
/// <summary>连接配置与密钥分离；旧版源仅在显式迁出时开启只读恢复。</summary>
public sealed class FileSourceFactory : IFileSourceFactory
{
    private readonly Dictionary<FileSourceKind, Func<FileSourceConfiguration, SourceCredentials, string, IFileSource>> _adapters = new();
    public FileSourceFactory()
    {
        Register(FileSourceKind.S3, (options, secrets, work) => new S3FileSource(options, secrets, work));
        Register(FileSourceKind.WebDav, (options, secrets, cache) => new WebDavFileSource(options, secrets, new WholeFileCache(cache)));
    }
    /// <summary>启动前注册未来适配器；不增加工程或修改统一契约。</summary>
    public void Register(FileSourceKind kind, Func<FileSourceConfiguration, SourceCredentials, string, IFileSource> adapter) => _adapters.Add(kind, adapter);
    public async Task<IFileSource> CreateAsync(MountDefinition mount, MountRepository repository, bool recovery, CancellationToken token)
    {
        var config = mount.Source ?? new(FileSourceKind.LegacyMock);
        if (config.Kind == FileSourceKind.LegacyMock)
        {
            if (!recovery) throw new NotSupportedException("旧版模拟挂载仅支持下载并移除或清空；请新建网络挂载。");
            return new LegacyRecoverySource(Path.Combine(repository.DataDirectory, "MockSources", mount.Id.ToString("N")));
        }
        if (!_adapters.TryGetValue(config.Kind, out var adapter)) throw new NotSupportedException("此协议尚未实现。");
        var cacheDirectory=Path.Combine(repository.DataDirectory,"RangeCache",mount.Id.ToString("N"));
        WholeFileCache.CleanupAbandoned(cacheDirectory);
        var source = adapter(config, new CredentialStore(repository.DataDirectory).Load(mount.Id), cacheDirectory);
        try { await source.ProbeAsync(token).ConfigureAwait(false); return source; }
        catch { await source.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <summary>按当前表单执行只读探测；不接触仓库、凭据密文、注册根或分片恢复。</summary>
    /// <param name="timeout">默认三十秒；调用者可取消，重复提交由界面防护。</param>
    public async Task<ConnectionTestResult> TestConnectionAsync(FileSourceConfiguration settings,SourceCredentials credentials,
        CancellationToken token=default,TimeSpan? timeout=null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout??TimeSpan.FromSeconds(30));
        var temporary=Path.Combine(Path.GetTempPath(),"HybridMount.ReadOnlyProbe",Guid.NewGuid().ToString("N"));
        try
        {
            if(!_adapters.TryGetValue(settings.Kind,out var adapter))throw new NotSupportedException("此协议尚未提供连接测试。");
            await using var source=adapter(settings,credentials,temporary);
            await source.ProbeAsync(deadline.Token).ConfigureAwait(false);
            return new(true,"只读连接测试成功；未保存挂载，也未验证远端写入权限。");
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested)
        { return new(false,"连接测试超时，请检查网络及服务响应。"); }
        catch(SourceConnectionException error) { return new(false,error.Message,error.Diagnostic); }
        catch(Exception error)when(error is IOException or ArgumentException or NotSupportedException)
        { return new(false,error.Message); }
    }
    /// <summary>禁止 URL 账户、查询和片段，HTTP 必须由用户显式启用；不接受重定向后的身份转发。</summary>
    public static Uri Endpoint(FileSourceConfiguration options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            (uri.Scheme == "http" && !options.AllowHttp) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入不含账户、查询或片段的 HTTPS 地址；明文 HTTP 需要显式允许。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}
