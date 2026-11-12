using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HybridMount.Models.Mounting.Synology;

/// <summary>群晖 File Station 会话；并发读取合并为经摘要验证的快照，写入仅允许已验证的新建。</summary>
/// <remarks>协议报文及认证只在本模块产生；宿主先停止调用，再异步释放会话。</remarks>
public sealed partial class SynologyFileSource : IFileSource,IRemoteChangeSource,IMountSourceRuntime,IMountNetworkSource,IUploadDiagnosticsSource,IRemoteUsageSource
{
    private readonly Uri _endpoint;
    private readonly string _root;
    private readonly FileSourceConfiguration _options;
    private readonly WholeFileCache _cache;
    private readonly string _connection=Guid.NewGuid().ToString("N");
    private readonly SharedAsyncWork<int,string> _logins=new(1);
    private readonly SharedAsyncWork<string,SourceEntry> _metadataQueries=new(128);
    private readonly SharedAsyncWork<string,string> _hashes=new(64);
    private readonly SemaphoreSlim _writes=new(1,1);
    private readonly ConcurrentDictionary<string,byte> _probePaths=new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifiedParents=new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockedParents=new(StringComparer.Ordinal);
    private readonly object _authenticationSync=new();
    private SourceConnectionException? _authenticationFailure;
    private Task? _dispose;
    /// <summary>首版不提供无可靠条件令牌的远端范围传输；本地完整文件缩略图仍由原生端处理。</summary>
    public FileSourceCapabilities Capabilities {get;}=new(CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,
        CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,SourceCaseSensitivity.Sensitive);
    /// <summary>新建按当前连接和目标父目录实测；其他破坏性操作保持不支持。</summary>
    public SourceWriteCapabilities WriteCapabilities {get;private set;}=new(CreateOnly:CapabilitySupport.Unknown,
        ConditionalUpdate:CapabilitySupport.Unsupported,ConditionalDelete:CapabilitySupport.Unsupported,ConditionalMove:CapabilitySupport.Unsupported,
        ExclusiveLock:CapabilitySupport.Unsupported,MultipartComplete:CapabilitySupport.Unsupported);
    public event Action<SourceContentBaseline>? ContentBaseline;
    public event Action<string>? WriteWarning;
    /// <summary>仅用于用户核对当前证书，不包含会话或登录参数。</summary>
    public string? ServerCertificateSha256=>_certificateSha256;
    /// <summary>设备凭据仅交付当前用户密钥存储；SID、OTP 和 SynoToken 不在此对象内。</summary>
    public SourceCredentials DeviceCredentials=>_credentials;
    /// <summary>创建独立协议会话；构造不发请求，cache 由本实例接管。</summary>
    public SynologyFileSource(FileSourceConfiguration options,SourceCredentials credentials,WholeFileCache cache,string? oneTimePassword=null)
        :this(options,credentials,cache,null,oneTimePassword) { }
    internal SynologyFileSource(FileSourceConfiguration options,SourceCredentials credentials,WholeFileCache cache,HttpMessageHandler? handler,string? oneTimePassword)
    {
        ValidateOptions(options);new SynologySourceProtocol().ValidateCredentials(options,credentials);
        _options=options;_endpoint=FileSourceFactory.Endpoint(options);_root=options.Prefix;_credentials=credentials;_cache=cache;_otpCode=oneTimePassword;
        _http=new HttpClient(handler??CreateHandler(NormalizeFingerprint(options.PinnedCertificateSha256))){Timeout=Timeout.InfiniteTimeSpan};
    }
    /// <summary>只读验证服务器、共享根及可选证书指纹；禁止路径越界和含凭据的地址。</summary>
    public static void ValidateOptions(FileSourceConfiguration options)
    {
        FileSourceFactory.Endpoint(options);
        if(options.Kind!=FileSourceKind.Synology||options.Prefix is null||!options.Prefix.StartsWith('/')||options.Prefix.Length<2||options.Prefix.EndsWith('/')||options.Prefix.Contains('\\')||options.Prefix.Split('/').Skip(1).Any(part=>part is "" or "." or ".."))
            throw new ArgumentException("请输入 DSM 服务地址和以 /共享文件夹 开头的远端根路径。");
        _=NormalizeFingerprint(options.PinnedCertificateSha256);
    }
    /// <summary>接受带冒号的 SHA-256 十六进制指纹；空值使用系统信任。</summary>
    public static string? NormalizeFingerprint(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        var normalized=value.Replace(":","",StringComparison.Ordinal).ToUpperInvariant();
        if(normalized.Length!=64||normalized.Any(c=>!char.IsAsciiHexDigit(c)))throw new ArgumentException("证书 SHA-256 指纹必须是 64 位十六进制字符，可包含冒号。");
        return normalized;
    }
    private string RemotePath(string relative)
    {relative=RelativePathRules.Normalize(relative);return relative.Length==0?_root:_root+"/"+relative;}
    private string RelativePath(string absolute)
    {
        if(absolute==_root)return "";
        if(!absolute.StartsWith(_root+"/",StringComparison.Ordinal))throw new IOException("群晖返回的路径越过挂载根。");
        return RelativePathRules.Normalize(absolute[(_root.Length+1)..]);
    }
    private static string Number(long value)=>value.ToString(CultureInfo.InvariantCulture);
    private static string Paths(string path)=>JsonSerializer.Serialize(new[]{path});
    private static JsonElement JsonArray(JsonElement data,string name)
        =>data.ValueKind==JsonValueKind.Object&&data.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Array?value:throw new IOException("群晖返回的文件列表格式无效。");
    private static int Total(JsonElement data)
        =>data.ValueKind==JsonValueKind.Object&&data.TryGetProperty("total",out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var count)&&count>=0?count:throw new IOException("群晖返回的分页总数无效。");
    private SourceEntry Entry(JsonElement file)
    {
        try
        {
            var path=RelativePath(file.GetProperty("path").GetString()!);var directory=file.GetProperty("isdir").GetBoolean();
            var additional=file.GetProperty("additional");var time=additional.GetProperty("time");
            var size=directory?0:additional.GetProperty("size").GetInt64();var modified=time.GetProperty("mtime").GetInt64();
            var created=time.TryGetProperty("crtime",out var creation)?creation.GetInt64():0;
            if(size<0)throw new IOException("群晖文件长度无效。");
            return new("",path,directory,size,DateTimeOffset.FromUnixTimeSeconds(modified),$"s:{Number(size)};m:{Number(modified)};c:{Number(created)}");
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        {throw new IOException("群晖文件元数据不完整或无法映射到本地路径。");}
    }
    /// <summary>摘要和观测代次均属于不透明内容版本，不能解释为服务器条件写令牌。</summary>
    public bool IsSameVersion(string left,string right)=>string.Equals(left,right,StringComparison.Ordinal);
    /// <summary>只比较大小和时间线索，不能据此认定文件正文相等；冷占位绑定仍须证明没有任何本地范围。</summary>
    public bool IsSameVersionHint(string expected,string observed)
    {
        static string Hint(string value){var marker=value.IndexOf(";md5:",StringComparison.Ordinal);return marker<0?value:value[..marker];}
        return Hint(expected)==Hint(observed);
    }
    /// <summary>只读登录并核验远端目录；此处不运行写能力探测。</summary>
    public async Task ProbeAsync(CancellationToken token=default)
    {await EnsureSessionAsync(token).ConfigureAwait(false);if(!(await GetMetadataAsync("",token).ConfigureAwait(false)).IsDirectory)throw new IOException("群晖挂载根必须是目录。");}
    private async Task<SourceEntry> FingerprintAsync(SourceEntry entry,CancellationToken token)
    {
        if(entry.IsDirectory)return entry;
        var digest=await Md5Async(entry.Path,token).ConfigureAwait(false);
        var after=await GetRawMetadataAsync(entry.Path,token).ConfigureAwait(false);
        if(after.IsDirectory||after.Version!=entry.Version)throw new SourceRescanRequiredException("群晖文件在内容核对期间变化，请重新核对。");
        return entry with{Version=entry.Version+";md5:"+digest};
    }
    /// <summary>文件元数据包含本轮远端摘要；不以同秒、同长度推断内容未变。</summary>
    public async Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();if(_probePaths.ContainsKey(path))throw new FileNotFoundException("本会话探测项尚未发布。");
        using var waiter=Share("metadata:"+path);
        return await _metadataQueries.RunAsync(path,cancel=>ReadMetadataOwnerAsync(path,cancel),
            token,Share("metadata:"+path)).ConfigureAwait(false);
    }
    /// <summary>共享元数据生产者独立保留整个查询；调用者取消不撤销其他消费者所需的认证、MD5及最终版本复查。</summary>
    private async Task<SourceEntry> ReadMetadataOwnerAsync(string path,CancellationToken token)
    {
        using var context=MountingHttpScheduler.EnterSourceOperation(_queueBinding,CloudOperationChannel.Metadata,path);
        using var flow=_queueBinding is null?null:await _queueBinding.Queue.AcquireCurrentWorkflowAsync(token).ConfigureAwait(false);
        return await FingerprintAsync(await GetRawMetadataAsync(path,token).ConfigureAwait(false),token).ConfigureAwait(false);
    }
    private async Task<SourceEntry> GetRawMetadataAsync(string path,CancellationToken token)
    {
        var data=await ApiAsync("SYNO.FileStation.List","getinfo",new(){["path"]=Paths(RemotePath(path)),["additional"]=Additional},false,token).ConfigureAwait(false);
        var files=JsonArray(data,"files");if(files.GetArrayLength()!=1)throw new IOException("群晖文件查询结果不完整。");
        var file=files[0];if(file.TryGetProperty("code",out var code)&&code.GetInt32()!=0)throw ApiFailure(code.GetInt32());
        var entry=Entry(file);if(entry.Path!=path)throw new IOException("群晖文件查询路径不一致。");return entry;
    }
    /// <summary>仅明确不存在返回空；认证、权限、网络及内容核对失败继续报告。</summary>
    public async Task<SourceEntry?> TryGetMetadataAsync(string path,CancellationToken token=default)
    {try{return await GetMetadataAsync(path,token).ConfigureAwait(false);}catch(FileNotFoundException){return null;}}
    /// <summary>完整分页仅返回本轮元数据，列表不逐文件查询MD5；未知摘要不证明内容相等。</summary>
    public async IAsyncEnumerable<SourceEntry> ListAsync(string path,[EnumeratorCancellation] CancellationToken token=default)
    {
        var offset=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while(true)
        {
            var data=await ApiAsync("SYNO.FileStation.List","list",new(){["folder_path"]=RemotePath(path),["offset"]=Number(offset),["limit"]="500",["additional"]=Additional,["sort_by"]="name",["sort_direction"]="asc"},false,token).ConfigureAwait(false);
            var files=JsonArray(data,"files");var total=Total(data);
            if(total<offset||files.GetArrayLength()>total-offset||files.GetArrayLength()==0&&offset<total)throw new SourceRescanRequiredException("群晖目录分页发生变化，请重新扫描。");
            foreach(var file in files.EnumerateArray())
            {
                var entry=Entry(file);
                if(entry.Path==path||RelativePathRules.Parent(entry.Path)!=path||!seen.Add(entry.Path)||seen.Count>1_000_000)
                    throw Failure("目录映射",ConnectionFailureKind.Service,"远端目录含重复、大小写碰撞、越界或过量路径；需要用户处理。");
                if(!_probePaths.ContainsKey(entry.Path))yield return entry;
            }
            offset=checked(offset+files.GetArrayLength());if(offset>=total)yield break;
        }
    }
    /// <summary>只查询共享卷容量，不将卷容量描述为账号配额。</summary>
    public async Task<RemoteCapacity> GetRemoteCapacityAsync(CancellationToken token=default)
    {
        var share=_root.Split('/')[1];var offset=0;
        while(true)
        {
            var data=await ApiAsync("SYNO.FileStation.List","list_share",new(){["additional"]="[\"volume_status\"]",["offset"]=Number(offset),["limit"]="500"},false,token).ConfigureAwait(false);
            var shares=JsonArray(data,"shares");var count=Total(data);
            if(count<offset||shares.GetArrayLength()>count-offset)throw new IOException("群晖共享文件夹分页不完整。");
            foreach(var item in shares.EnumerateArray())
            {
                if(item.GetProperty("name").GetString()!=share)continue;
                if(!item.TryGetProperty("additional",out var additional)||!additional.TryGetProperty("volume_status",out var volume))return RemoteCapacity.Unknown;
                // 待实测：共享文件夹 volume_status 报告的是卷可用空间，不一定包含账号配额。
                var free=volume.TryGetProperty("freespace",out var f)?RemoteCapacity.ParseBytes(f.ToString()):null;
                var total=volume.TryGetProperty("totalspace",out var t)?RemoteCapacity.ParseBytes(t.ToString()):null;
                return new("Synology 卷（非账号配额）",free,total is {} size&&free is {} available&&size>=available?size-available:null);
            }
            offset=checked(offset+shares.GetArrayLength());if(offset>=count)return RemoteCapacity.Unknown;
            if(shares.GetArrayLength()==0)throw new IOException("群晖共享文件夹分页不完整。");
        }
    }
    public async Task<IReadOnlyList<SynologySharedFolder>> ListSharedFoldersAsync(CancellationToken token=default)
    {
        var folders=new List<SynologySharedFolder>();var seen=new HashSet<string>(StringComparer.Ordinal);var offset=0;
        while(true)
        {
            var data=await ApiAsync("SYNO.FileStation.List","list_share",new(){["offset"]=Number(offset),["limit"]="500"},false,token).ConfigureAwait(false);
            var shares=JsonArray(data,"shares");var total=Total(data);
            if(total<offset||shares.GetArrayLength()>total-offset||shares.GetArrayLength()==0&&offset<total)throw new IOException("群晖共享文件夹分页不完整。");
            foreach(var item in shares.EnumerateArray())
            {
                if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("name",out var n)||n.ValueKind!=JsonValueKind.String||!item.TryGetProperty("path",out var p)||p.ValueKind!=JsonValueKind.String)throw new IOException("群晖共享文件夹列表无效。");
                var name=n.GetString();var path=p.GetString();
                if(string.IsNullOrEmpty(name)||path is null||path!="/"+name||name.Contains('/')||name is "." or ".."||!seen.Add(path)||seen.Count>10000)throw new IOException("群晖共享文件夹列表无效。");
                folders.Add(new(name,path));
            }
            offset=checked(offset+shares.GetArrayLength());if(offset>=total)return folders;
        }
    }
    /// <summary>File Station 首版使用完整轮询，不伪造增量游标。</summary>
    public Task<bool> SupportsCollectionSyncAsync(string collection,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(false);}
    public Task<RemoteChangeBatch> GetCollectionChangesAsync(string collection,string? syncToken,CancellationToken token)=>throw new NotSupportedException("群晖使用完整目录轮询。");
    public SourceEntry WithObservation(SourceEntry entry,long observation)=>entry;
    public void RestoreObservations(IEnumerable<RemoteShadowEntry> entries) { }
    public bool HasReliableVersion(SourceEntry entry)=>entry.IsDirectory||entry.Version.Contains(";md5:",StringComparison.Ordinal);
    string IMountSourceRuntime.RemoteScope(long revision)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Synology:"+_endpoint.AbsoluteUri+"\0"+_root+"\0"+Number(revision))));
    string? IMountSourceRuntime.ConnectionDiagnostics=>"群晖：完整摘要快照；仅联机预览不支持；覆盖、删除、移动不支持；认证"+(_authenticationFailure is null?"有效或未登录":"需用户处理")+$"；快照在途={_cache.VerifiedWorkCount}／排队={_cache.VerifiedQueuedCount}；整文件下载={_snapshotMetrics.GetValueOrDefault("downloads")}／发布={_snapshotMetrics.GetValueOrDefault("published")}／命中={_snapshotMetrics.GetValueOrDefault("cacheHits")}／超时={_snapshotMetrics.GetValueOrDefault("timeouts")}；排队ms={_snapshotMetrics.GetValueOrDefault("queueMs")}／元数据ms={_snapshotMetrics.GetValueOrDefault("metadataMs")}／正文ms={_snapshotMetrics.GetValueOrDefault("bodyMs")}／验证ms={_snapshotMetrics.GetValueOrDefault("verificationMs")}／总ms={_snapshotMetrics.GetValueOrDefault("totalMs")}／字节={_snapshotMetrics.GetValueOrDefault("bytes")}";
    void IMountSourceRuntime.AttachRuntime(MountDefinition mount,MountRepository repository)
    {_saveCredentials=credentials=>new CredentialStore(repository.DataDirectory).Save(mount.Id,credentials);}
    MountQueueBinding? IMountNetworkSource.QueueBinding=>_queueBinding;
    void IMountNetworkSource.BindQueue(MountQueueBinding binding)=>_queueBinding=binding;
    private void PublishBaseline(SourceContentBaseline baseline)
    {if(ContentBaseline is {} listeners)foreach(Action<SourceContentBaseline> listener in listeners.GetInvocationList())try{listener(baseline);}catch{}}
    private void Warn(string message)
    {if(WriteWarning is {} listeners)foreach(Action<string> listener in listeners.GetInvocationList())try{listener(message);}catch{}}
    /// <summary>后台统计仍走完整目录枚举，受维护任务优先级和取消约束。</summary>
    public async Task<RemoteUsage> GetRemoteUsageAsync(CancellationToken token=default)
    {
        var queue=new Queue<string>();queue.Enqueue("");var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long used=0;
        while(queue.Count!=0)
        {
            await foreach(var entry in ListAsync(queue.Dequeue(),token).ConfigureAwait(false))
            {
                if(!seen.Add(entry.Path)||seen.Count>1_000_000)throw new IOException("群晖用量统计包含重复路径或超过安全数量限制。");
                if(entry.IsDirectory)queue.Enqueue(entry.Path);else used=checked(used+entry.Size);
            }
        }
        token.ThrowIfCancellationRequested();return new("群晖挂载根下的当前文件",used);
    }
    /// <summary>停止共享读取并等候资源归还，再有界退出登录和释放 HTTP；重复释放共用任务。</summary>
    public ValueTask DisposeAsync(){lock(_authenticationSync)return new(_dispose??=DisposeCoreAsync());}
    private async Task DisposeCoreAsync()
    {
        await _cache.DisposeAsync().ConfigureAwait(false);await _metadataQueries.DisposeAsync().ConfigureAwait(false);await _hashes.DisposeAsync().ConfigureAwait(false);await _logins.DisposeAsync().ConfigureAwait(false);
        var sid=_sid;_sid=null;
        if(sid is not null&&_apis.ContainsKey("SYNO.API.Auth"))
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try{await SendJsonAsync(ApiAddress("SYNO.API.Auth"),Fields("SYNO.API.Auth","logout",new(),sid),false,timeout.Token).ConfigureAwait(false);}
            catch(Exception error)when(error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        _otpCode=null;_synoToken=null;_saveCredentials=null;_credentials=new();_http.Dispose();_writes.Dispose();
    }
}
