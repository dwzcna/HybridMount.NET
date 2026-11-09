using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.WebDAV;

public sealed partial class WebDavFileSource
{
    private readonly ConcurrentDictionary<string,RemoteTemporaryItem> _temporary=new(StringComparer.Ordinal);
    private MountRepository? _temporaryRepository;
    private Guid _temporaryMount;
    private readonly CancellationTokenSource _temporaryStop=new();
    private readonly object _temporaryCleanupSync=new();
    private readonly HashSet<string> _temporaryCleanupQueued=new(StringComparer.Ordinal);
    private Task _temporaryCleanupWork=Task.CompletedTask;
    internal TimeSpan TemporaryRetryDelay {get;set;}=TimeSpan.FromSeconds(30);
    internal Task DrainTemporaryCleanupAsync(CancellationToken token=default)
    {lock(_temporaryCleanupSync)return _temporaryCleanupWork.WaitAsync(token);}
    /// <summary>供恢复状态和验收读取的快照，不包含凭据或锁令牌。</summary>
    public IReadOnlyList<RemoteTemporaryItem> PendingTemporaryItems=>_temporary.Values.ToArray();
    private string _temporaryScope="";
    internal string RemoteSyncIdentity=>_temporaryScope;
    internal string RemoteScope(long revision)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_temporaryScope+"\0"+revision)));
    private static readonly string[] ProbeHashes=[Digest([17,31]),Digest([47]),Digest([99]),Digest([])];
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>工厂为真实挂载绑定持久日志；测试连接不调用此入口，不产生清理或写入副作用。</summary>
    public void AttachTemporaryJournal(MountDefinition mount,MountRepository repository)
    {
        _temporaryMount=mount.Id;_temporaryRepository=repository;
        foreach(var item in repository.TemporaryItems(mount.Id,_temporaryScope))
            if(IsTemporaryPath(item.Path))
            {
                _temporary[item.Path]=item;
                if(item.Purpose=="probe")_sessionProbePaths.TryAdd(item.Path,0);
            }
    }
    /// <summary>接受新普通名称及旧隐藏名称；匹配仅用于读取精确日志，不赋予按前缀清理权限。</summary>
    private static bool IsTemporaryPath(string path)=>Regex.IsMatch(RelativePaths.Normalize(path).Split('/')[^1],@"^\.?hybridmount-(probe|upload)-[a-f0-9]{32}(-(destination|new))?$",RegexOptions.CultureInvariant);
    private void SaveTemporary(RemoteTemporaryItem item){_temporaryRepository?.SaveTemporary(item);_temporary[item.Path]=item;}
    private string TrackTemporary(string path,string purpose="probe",string[]? hashes=null)
    {
        path=RelativePaths.Normalize(path);if(!IsTemporaryPath(path))throw new IOException("临时项路径不符合本次操作规则。");
        if(purpose=="probe")_sessionProbePaths.TryAdd(path,0);
        if(!_temporary.ContainsKey(path))SaveTemporary(new(_temporaryMount,_temporaryScope,path,purpose,"Planned",hashes??ProbeHashes));return path;
    }
    private void CompleteTemporary(string path){_temporaryRepository?.CompleteTemporary(_temporaryMount,_temporaryScope,path);_temporary.TryRemove(path,out _);}
    private string? TemporaryRequestPath(Uri? uri)
    {if(uri is null||uri.Scheme!=_endpoint.Scheme||uri.Authority!=_endpoint.Authority||!uri.AbsolutePath.StartsWith(_endpoint.AbsolutePath,StringComparison.Ordinal))return null;
        var path=string.Join('/',uri.AbsolutePath[_endpoint.AbsolutePath.Length..].TrimEnd('/').Split('/').Select(Uri.UnescapeDataString));return _temporary.ContainsKey(path)?path:null;}
    /// <summary>首次创建先确认不存在；持久意图先于网络请求，412 或外部内容不能取得清理所有权。</summary>
    private async Task PrepareTemporaryRequestAsync(HttpRequestMessage request,CancellationToken token)
    {
        if(request.Method.Method is not("PUT" or "LOCK" or "MOVE"))return;
        var path=TemporaryRequestPath(request.RequestUri);
        if(request.Method.Method=="MOVE"&&request.Headers.TryGetValues("Destination",out var values)&&Uri.TryCreate(values.Single(),UriKind.Absolute,out var destination))path=TemporaryRequestPath(destination);
        if(path is null||!_temporary.TryGetValue(path,out var item)||item.Stage!="Planned")return;
        if(await TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)
        {SaveTemporary(item with{Stage="Foreign",Error="创建前远端已存在，未取得清理所有权。"});throw new SourceConflictException("随机临时目标已存在，未覆盖或删除。");}
        SaveTemporary(item with{Stage="Sending"});
    }
    private async Task ObserveTemporaryResponseAsync(HttpRequestMessage request,HttpResponseMessage response,CancellationToken token)
    {
        if(request.Method.Method is not("PUT" or "LOCK" or "MOVE"))return;
        var path=TemporaryRequestPath(request.RequestUri);
        if(request.Method.Method=="MOVE"&&request.Headers.TryGetValues("Destination",out var values)&&Uri.TryCreate(values.Single(),UriKind.Absolute,out var destination))path=TemporaryRequestPath(destination);
        if(path is null||!_temporary.TryGetValue(path,out var item))return;
        if(response.IsSuccessStatusCode)
        {
            item=item with{Stage="Owned",ETag=NormalizeTag(response.Headers.TryGetValues("ETag",out var tags)?tags.FirstOrDefault():null),Size=null,ModifiedUtc=null};SaveTemporary(item);
            using var capture=CancellationTokenSource.CreateLinkedTokenSource(token);capture.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var metadata=await TryGetMetadataAsync(path,capture.Token).ConfigureAwait(false);if(metadata is null)return;
                if(metadata.IsDirectory||Strong(item.ETag)&&NormalizeTag(Version(metadata).ETag)!=item.ETag)
                {SaveTemporary(item with{Stage="Foreign",Error="临时项创建后版本变化，需要人工核对。"});throw new SourceConflictException("临时项创建后版本变化。");}
                // 后读元数据只能补充提示，不能替代缺失的写回回执取得所有权。
                SaveTemporary(item with{Size=metadata.Size,ModifiedUtc=metadata.ModifiedUtc==DateTimeOffset.UnixEpoch?null:metadata.ModifiedUtc});
            }
            catch(Exception error)when(error is OperationCanceledException||error is IOException and not SourceConflictException){ }
        }
        else if(item.Stage=="Sending"&&response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            SaveTemporary(item with{Stage="Foreign",Error="首次创建被明确拒绝，未清理目标。"});
    }
    /// <summary>只恢复精确日志路径；验证强版本或已知摘要，内容变化时保留并提示，不按名称前缀删除。</summary>
    public async Task RecoverTemporaryItemsAsync(CancellationToken token=default)
    {
        foreach(var path in _temporary.Keys.ToArray()){token.ThrowIfCancellationRequested();await CleanupTemporaryAsync(path,token).ConfigureAwait(false);}
    }
    /// <summary>强验证器最多核对三次；缺失、弱及裸标签只等待，不升级为可靠条件。</summary>
    private async Task<SourceEntry?> ReadCleanupMetadataAsync(string path,RemoteTemporaryItem item,CancellationToken token)
    {
        for(var attempt=0;attempt<3;attempt++)
        {
            token.ThrowIfCancellationRequested();
            var metadata=await TryGetMetadataAsync(path,token).ConfigureAwait(false);
            if(metadata is null)return null;
            if(item.Stage=="Planned"||metadata.IsDirectory)throw new SourceConflictException("未发送创建或目标为目录，保留远端内容。");
            if(Strong(Version(metadata).ETag))return metadata;
            if(attempt<2)await Task.Delay(attempt==0?250:750,token).ConfigureAwait(false);
        }
        throw new IOException("临时项未提供可靠强ETag，保留日志待重连核对。");
    }
    private async Task DeleteTemporaryAsync(string path,SourceEntry metadata,RemoteTemporaryItem item,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Delete,Address(path));var tag=NormalizeTag(Version(metadata).ETag);
        if(!Strong(tag))throw new IOException("临时项缺少可靠强版本，保留日志待核对。");
        request.Headers.IfMatch.ParseAdd(tag!);
        using var response=await SendAsync(request,token,true).ConfigureAwait(false);
        if(response.StatusCode==HttpStatusCode.PreconditionFailed)throw new SourceConflictException("临时项条件删除时版本变化，保留远端内容。");
        if(!response.IsSuccessStatusCode&&response.StatusCode!=HttpStatusCode.NotFound)throw new IOException($"清理返回 HTTP {(int)response.StatusCode}。");
        CompleteTemporary(path);
    }
    private void TemporaryCleanupFailed(RemoteTemporaryItem item,Exception error)
    {
        SaveTemporary(item with{Stage=error is SourceConflictException?"Foreign":"CleanupPending",Error=error is SourceConflictException?"身份或内容变化，需要人工核对。":item.HashAttempts>=5?"摘要核对已达五次，请人工核对临时项。":"清理未完成，将在重连时核对重试。"});Warn("DAV 临时项清理待处理："+item.Path);
    }
    private void QueueTemporaryHash(RemoteTemporaryItem item,SourceEntry metadata)
    {
        lock(_temporaryCleanupSync)
        {
            if(_temporaryStop.IsCancellationRequested||_temporaryCleanupQueued.Contains(item.Path)||item.HashAttempts>=5||item.NextHashAttemptUtc>DateTimeOffset.UtcNow)return;
            item=item with{Stage="CleanupPending",HashAttempts=item.HashAttempts+1,NextHashAttemptUtc=DateTimeOffset.UtcNow.AddMilliseconds(Math.Min(300000,TemporaryRetryDelay.TotalMilliseconds*(1<<item.HashAttempts))),Error="摘要核对已延后，不影响连接。"};SaveTemporary(item);
            _temporaryCleanupQueued.Add(item.Path);var previous=_temporaryCleanupWork;
            _temporaryCleanupWork=Task.Run(async()=>
            {
                try
                {
                    await previous.ConfigureAwait(false);var token=_temporaryStop.Token;token.ThrowIfCancellationRequested();var tag=NormalizeTag(Version(metadata).ETag)!;
                    var digest=await ReadContentHashAsync(item.Path,null,token,tag).ConfigureAwait(false);
                    if(!item.Hashes.Contains(digest,StringComparer.Ordinal))throw new SourceConflictException("临时项内容已改变，保留远端内容。");
                    var after=await TryGetMetadataAsync(item.Path,token).ConfigureAwait(false);if(after is null){CompleteTemporary(item.Path);return;}
                    if(!IsSameVersion(metadata.Version,after.Version)||NormalizeTag(Version(after).ETag)!=tag)throw new SourceConflictException("临时项核对期间版本变化。");
                    item=item with{ETag=tag,Size=after.Size,ModifiedUtc=after.ModifiedUtc};SaveTemporary(item);
                    await DeleteTemporaryAsync(item.Path,after,item,token).ConfigureAwait(false);
                }
                catch(Exception error)when(error is IOException or OperationCanceledException){TemporaryCleanupFailed(item,error);}
                finally{lock(_temporaryCleanupSync)_temporaryCleanupQueued.Remove(item.Path);}
            });
        }
    }
    /// <summary>五秒只覆盖元数据及条件删除；旧记录的摘要读取由独立后台工作按无进展超时核对。</summary>
    private async Task CleanupTemporaryAsync(string path,CancellationToken token)
    {
        if(!_temporary.TryGetValue(path,out var item)||item.Stage=="Foreign")return;
        lock(_temporaryCleanupSync)if(_temporaryCleanupQueued.Contains(path))return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var cleanup=deadline.Token;
        try
        {
            var metadata=await ReadCleanupMetadataAsync(path,item,cleanup).ConfigureAwait(false);
            if(metadata is null){CompleteTemporary(path);return;}
            if(Strong(item.ETag))
            {
                if(NormalizeTag(Version(metadata).ETag)!=item.ETag||item.Size is {} size&&size!=metadata.Size)throw new SourceConflictException("临时项版本已改变，保留远端内容。");
            }
            else
            {
                if(item.Size is {} size&&size!=metadata.Size||item.ModifiedUtc is {} modified&&modified!=metadata.ModifiedUtc)
                    throw new SourceConflictException("临时项大小或时间已改变，保留远端内容。");
                QueueTemporaryHash(item,metadata);return;
            }
            await DeleteTemporaryAsync(path,metadata,item,cleanup).ConfigureAwait(false);
        }
        catch(Exception error)when(error is IOException or OperationCanceledException)
        {TemporaryCleanupFailed(item,error);}
    }
}
