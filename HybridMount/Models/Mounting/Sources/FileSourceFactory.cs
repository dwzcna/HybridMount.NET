using HybridMount.Commons;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.Sources;

/// <summary>宿主只依赖工厂；未注册的 SMB/NFS 明确失败，不回退为其他协议。</summary>
public interface IFileSourceFactory
{
    Task<IFileSource> CreateAsync(MountDefinition mount, MountRepository repository, bool recovery, CancellationToken token);
}
/// <summary>连接配置与密钥分离；旧版源仅在显式迁出时开启只读恢复。</summary>
public sealed class FileSourceFactory : IFileSourceFactory
{
    private static readonly IReadOnlyDictionary<FileSourceKind,IFileSourceProtocol> Protocols=new Dictionary<FileSourceKind,IFileSourceProtocol>
    {[FileSourceKind.WebDav]=new WebDavSourceProtocol(),[FileSourceKind.S3]=new S3SourceProtocol()};
    private readonly Dictionary<FileSourceKind, Func<FileSourceConfiguration, SourceCredentials, string, IFileSource>> _adapters = new();
    public FileSourceFactory()
    {
        Register(FileSourceKind.S3, Protocols[FileSourceKind.S3].Create);
        Register(FileSourceKind.WebDav, Protocols[FileSourceKind.WebDav].Create);
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
        try
        {
            if(source is IMountNetworkSource network&&TasksQueueService.CurrentBinding is {} binding)network.BindQueue(binding);
            if(source is IMountSourceRuntime runtime)runtime.AttachRuntime(mount,repository);
            await source.ProbeAsync(token).ConfigureAwait(false);return source;
        }
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
        catch(OperationCanceledException error)when(!token.IsCancellationRequested)
        {
            return Protocols.TryGetValue(settings.Kind,out var protocol)?protocol.Timeout(error):new(false,"连接测试超时，请检查网络及服务响应。");
        }
        catch(SourceConnectionException error) { return new(false,error.Message,error.Diagnostic); }
        catch(Exception error)when(error is IOException or ArgumentException or NotSupportedException)
        { return new(false,error.Message); }
    }
    /// <summary>公共层通过适配策略校验，SDK／签名／条件规则留在对应模块；不建立连接。</summary>
    internal static void ValidateProtocolOptions(FileSourceConfiguration options)=>Protocol(options.Kind).ValidateOptions(options);
    internal static void ValidateProtocolCredentials(FileSourceConfiguration options,SourceCredentials credentials)=>Protocol(options.Kind).ValidateCredentials(options,credentials);
    /// <summary>配置层只组合迁移结果；秘密模式及额外凭据的 DPAPI 目录由协议策略拥有。</summary>
    internal static SourceCredentials BackupCredentials(MountDefinition mount,string directory)=>Protocol(mount.Source!.Kind).BackupCredentials(mount,directory);
    internal static SourceCredentials NormalizeImportCredentials(FileSourceConfiguration source,SourceCredentials keys)=>Protocol(source.Kind).NormalizeImportCredentials(source,keys);
    internal static SourceCredentials? BackupAdditionalCredentials(MountDefinition mount,string directory)=>Protocol(mount.Source!.Kind).BackupAdditionalCredentials(mount,directory);
    internal static void SaveAdditionalCredentials(MountDefinition mount,SourceCredentials? keys,string directory)
    {if(mount.Source is {} source&&Protocols.TryGetValue(source.Kind,out var protocol))protocol.SaveAdditionalCredentials(mount,keys,directory);}
    internal static void DeleteAdditionalCredentials(MountDefinition mount,string directory)
    {if(mount.Source is {} source&&Protocols.TryGetValue(source.Kind,out var protocol))protocol.DeleteAdditionalCredentials(mount.Id,directory);}
    internal static void ValidateBackupFields(MountBackupItem item)
    {foreach(var protocol in Protocols.Values)protocol.ValidateBackupFields(item);}
    internal static string CapacityWriteScope(MountDefinition definition)=>definition.Source is {Kind:FileSourceKind.WebDav or FileSourceKind.S3} source?Protocol(source.Kind).CapacityWriteScope(source):definition.Id.ToString("N");
    private static IFileSourceProtocol Protocol(FileSourceKind kind)=>Protocols.TryGetValue(kind,out var value)?value:throw new NotSupportedException("此协议尚未实现。");
    /// <summary>禁止 URL 账户、查询和片段，HTTP 必须由用户显式启用；不接受重定向后的身份转发。</summary>
    public static Uri Endpoint(FileSourceConfiguration options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            (uri.Scheme == "http" && !options.AllowHttp) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入不含账户、查询或片段的 HTTPS 地址；明文 HTTP 需要显式允许。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}
