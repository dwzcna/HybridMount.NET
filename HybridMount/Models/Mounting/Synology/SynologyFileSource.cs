using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HybridMount.Models.Mounting.Synology;

public sealed partial class SynologyFileSource : IFileSource,IRemoteChangeSource,IMountSourceRuntime,IMountNetworkSource,IUploadDiagnosticsSource,IRemoteUsageSource
{
    private readonly Uri _endpoint;
    private readonly string _root;
    private readonly WholeFileCache _cache;
    private readonly SemaphoreSlim _writes=new(1,1);
    public FileSourceCapabilities Capabilities {get;private set;}=new(CapabilitySupport.Unknown,CapabilitySupport.Unsupported,
        CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,SourceCaseSensitivity.Sensitive);
    public SourceWriteCapabilities WriteCapabilities {get;}=new(CreateOnly:CapabilitySupport.Unknown,
        ConditionalUpdate:CapabilitySupport.Unsupported,ConditionalDelete:CapabilitySupport.Unsupported,ConditionalMove:CapabilitySupport.Unsupported,
        ExclusiveLock:CapabilitySupport.Unsupported,MultipartComplete:CapabilitySupport.Unsupported);
    public event Action<SourceContentBaseline>? ContentBaseline;
    public event Action<string>? WriteWarning;
    public string? ServerCertificateSha256=>_certificateSha256;
    public SourceCredentials DeviceCredentials=>_credentials;

    public SynologyFileSource(FileSourceConfiguration options,SourceCredentials credentials,WholeFileCache cache,string? oneTimePassword=null)
        :this(options,credentials,cache,null,oneTimePassword) { }
    internal SynologyFileSource(FileSourceConfiguration options,SourceCredentials credentials,WholeFileCache cache,HttpMessageHandler? handler,string? oneTimePassword)
    {
        ValidateOptions(options);new SynologySourceProtocol().ValidateCredentials(options,credentials);
        _endpoint=FileSourceFactory.Endpoint(options);_root=options.Prefix;_credentials=credentials;_cache=cache;_otpCode=oneTimePassword;
        _http=new HttpClient(handler??CreateHandler(NormalizeFingerprint(options.PinnedCertificateSha256))){Timeout=Timeout.InfiniteTimeSpan};
    }

    public static void ValidateOptions(FileSourceConfiguration options)
    {
        FileSourceFactory.Endpoint(options);
        if(!options.Prefix.StartsWith('/')||options.Prefix.Length<2||options.Prefix.EndsWith('/')||options.Prefix.Contains('\\')||options.Prefix.Split('/').Skip(1).Any(part=>part is "" or "." or ".."))
            throw new ArgumentException("请输入 DSM 服务地址和以 /共享文件夹 开头的远端根路径。");
        _=NormalizeFingerprint(options.PinnedCertificateSha256);
    }
    public static string? NormalizeFingerprint(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        var normalized=value.Replace(":", "",StringComparison.Ordinal).ToUpperInvariant();
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
            // 待实测：DSM mtime/crtime 的秒级精度及缺少 crtime 时的回退；它们不是强内容版本。
            return new("",path,directory,size,DateTimeOffset.FromUnixTimeSeconds(modified),$"s:{Number(size)};m:{Number(modified)};c:{Number(created)}");
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        {throw new IOException("群晖文件元数据不完整或无法映射到本地路径。");}
    }
    public bool IsSameVersion(string left,string right)=>string.Equals(left,right,StringComparison.Ordinal);
    public async Task ProbeAsync(CancellationToken token=default)
    {await EnsureSessionAsync(token).ConfigureAwait(false);if(!(await GetMetadataAsync("",token).ConfigureAwait(false)).IsDirectory)throw new IOException("群晖挂载根必须是目录。");}
    private static bool IsStagingPath(string path)=>path.Split('/').Any(part=>part.StartsWith(".hm-",StringComparison.OrdinalIgnoreCase)&&part.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase));
    public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)
    {token.ThrowIfCancellationRequested();if(IsStagingPath(path))throw new FileNotFoundException("群晖临时上传文件不属于挂载内容。");return GetRawMetadataAsync(path,token);}
    private async Task<SourceEntry> GetRawMetadataAsync(string path,CancellationToken token)
    {
        try
        {
            var data=await ApiAsync("SYNO.FileStation.List","getinfo",new(){["path"]=Paths(RemotePath(path)),["additional"]=Additional},false,token).ConfigureAwait(false);
            var files=JsonArray(data,"files");
            if(files.GetArrayLength()!=1)throw new IOException("群晖文件查询结果不完整。");
            var file=files[0];
            if(file.TryGetProperty("code",out var code)&&code.GetInt32()!=0)throw new SynologyApiException(code.GetInt32());
            var entry=Entry(file);if(entry.Path!=path)throw new IOException("群晖文件查询路径不一致。");return entry;
        }
        catch(SynologyApiException error)when(error.Code==408){throw new FileNotFoundException("群晖文件不存在。");}
    }
    public async Task<SourceEntry?> TryGetMetadataAsync(string path,CancellationToken token=default)
    {try{return await GetMetadataAsync(path,token).ConfigureAwait(false);}catch(FileNotFoundException){return null;}}
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
                if(entry.Path==path||RelativePathRules.Parent(entry.Path)!=path||!seen.Add(entry.Path)||seen.Count>1_000_000)throw new SourceRescanRequiredException("群晖目录包含重复、过量或无法映射的路径。");
                if(!IsStagingPath(entry.Path))yield return entry;
            }
            offset=checked(offset+files.GetArrayLength());if(offset>=total)yield break;
        }
    }
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
    public Task<bool> SupportsCollectionSyncAsync(string collection,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(false);}
    public Task<RemoteChangeBatch> GetCollectionChangesAsync(string collection,string? syncToken,CancellationToken token)=>throw new NotSupportedException("群晖使用完整目录轮询。");
    public SourceEntry WithObservation(SourceEntry entry,long observation)=>entry;
    public void RestoreObservations(IEnumerable<RemoteShadowEntry> entries) { }
    public bool HasReliableVersion(SourceEntry entry)=>false;
    string IMountSourceRuntime.RemoteScope(long revision)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Synology:"+_endpoint.AbsoluteUri+"\0"+_root+"\0"+Number(revision))));
    void IMountSourceRuntime.AttachRuntime(MountDefinition mount,MountRepository repository)
    {_saveCredentials=credentials=>new CredentialStore(repository.DataDirectory).Save(mount.Id,credentials);}
    MountQueueBinding? IMountNetworkSource.QueueBinding=>_queueBinding;
    void IMountNetworkSource.BindQueue(MountQueueBinding binding)=>_queueBinding=binding;
    private void WarnWeakWrite()
    {if(WriteWarning is {} listeners)foreach(Action<string> listener in listeners.GetInvocationList())try{listener("群晖采用弱条件写入；核对与提交之间仍可能发生外部修改。");}catch{}}
    private void PublishBaseline(SourceContentBaseline baseline)
    {if(ContentBaseline is {} listeners)foreach(Action<SourceContentBaseline> listener in listeners.GetInvocationList())try{listener(baseline);}catch{}}
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
    public async ValueTask DisposeAsync()
    {await _cache.DisposeAsync().ConfigureAwait(false);_sid=null;_otpCode=null;_saveCredentials=null;_http.Dispose();_sessionGate.Dispose();_writes.Dispose();}
}
