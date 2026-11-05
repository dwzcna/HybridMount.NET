using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class WebDavFileSource
{
    private readonly ConcurrentDictionary<string,RemoteTemporaryItem> _temporary=new(StringComparer.Ordinal);
    private MountRepository? _temporaryRepository;
    private Guid _temporaryMount;
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
            if(IsTemporaryPath(item.Path))_temporary[item.Path]=item;
    }
    /// <summary>接受新普通名称及旧隐藏名称；匹配仅用于读取精确日志，不赋予按前缀清理权限。</summary>
    private static bool IsTemporaryPath(string path)=>Regex.IsMatch(RelativePaths.Normalize(path).Split('/')[^1],@"^\.?hybridmount-(probe|upload)-[a-f0-9]{32}(-(destination|new))?$",RegexOptions.CultureInvariant);
    private void SaveTemporary(RemoteTemporaryItem item){_temporaryRepository?.SaveTemporary(item);_temporary[item.Path]=item;}
    private string TrackTemporary(string path,string purpose="probe",string[]? hashes=null)
    {
        path=RelativePaths.Normalize(path);if(!IsTemporaryPath(path))throw new IOException("临时项路径不符合本次操作规则。");
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
    private void ObserveTemporaryResponse(HttpRequestMessage request,HttpResponseMessage response)
    {
        if(request.Method.Method is not("PUT" or "LOCK" or "MOVE"))return;
        var path=TemporaryRequestPath(request.RequestUri);
        if(request.Method.Method=="MOVE"&&request.Headers.TryGetValues("Destination",out var values)&&Uri.TryCreate(values.Single(),UriKind.Absolute,out var destination))path=TemporaryRequestPath(destination);
        if(path is null||!_temporary.TryGetValue(path,out var item))return;
        if(response.IsSuccessStatusCode)SaveTemporary(item with{Stage="Owned",ETag=NormalizeTag(response.Headers.TryGetValues("ETag",out var tags)?tags.FirstOrDefault():null)});
        else if(item.Stage=="Sending"&&response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            SaveTemporary(item with{Stage="Foreign",Error="首次创建被明确拒绝，未清理目标。"});
    }
    /// <summary>只恢复精确日志路径；验证强版本或已知摘要，内容变化时保留并提示，不按名称前缀删除。</summary>
    public async Task RecoverTemporaryItemsAsync(CancellationToken token=default)
    {
        foreach(var path in _temporary.Keys.ToArray()){token.ThrowIfCancellationRequested();await CleanupTemporaryAsync(path,token).ConfigureAwait(false);}
    }
    private async Task CleanupTemporaryAsync(string path,CancellationToken token)
    {
        if(!_temporary.TryGetValue(path,out var item)||item.Stage=="Foreign")return;
        try
        {
            var metadata=await TryGetMetadataAsync(path,token).ConfigureAwait(false);
            if(metadata is null){CompleteTemporary(path);return;}
            if(item.Stage=="Planned"||metadata.IsDirectory)throw new SourceConflictException("未发送创建或目标为目录，保留远端内容。");
            var tag=NormalizeTag(Version(metadata).ETag);
            var reliable=Strong(item.ETag)&&tag==item.ETag;
            var digest=reliable?null:await ReadContentHashAsync(path,null,token).ConfigureAwait(false);
            if(!reliable&&!item.Hashes.Contains(digest,StringComparer.Ordinal))
                throw new SourceConflictException("临时项内容已改变，保留远端内容。");
            var after=await TryGetMetadataAsync(path,token).ConfigureAwait(false);
            if(after is null){CompleteTemporary(path);return;}
            if(!IsSameVersion(metadata.Version,after.Version))throw new SourceConflictException("临时项核对期间版本变化。");
            using var request=new HttpRequestMessage(HttpMethod.Delete,Address(path));if(Strong(tag))request.Headers.IfMatch.ParseAdd(tag!);
            using var response=await SendAsync(request,token,true).ConfigureAwait(false);
            if(!response.IsSuccessStatusCode&&response.StatusCode!=HttpStatusCode.NotFound)throw new IOException($"清理返回 HTTP {(int)response.StatusCode}。");
            CompleteTemporary(path);
        }
        catch(Exception error)when(error is IOException or OperationCanceledException)
        {SaveTemporary(item with{Stage=error is SourceConflictException?"Foreign":"CleanupPending",Error=error is SourceConflictException?"身份或内容变化，需要人工核对。":"清理未完成，将在重连时核对重试。"});Warn("DAV 临时项清理待处理："+path);}
    }
}
