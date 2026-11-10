using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HybridMount.Models.Mounting.Synology;

/// <summary>新建能力按连接和父目录探测；任何覆盖、删除或移动用户对象的调用在发送前拒绝。</summary>
public sealed partial class SynologyFileSource
{
    private static string NonRoot(string path)=>RelativePathRules.Normalize(path) is {Length:>0} normalized?normalized:throw new ArgumentException("不能修改群晖挂载根。");
    private static string Name(string path)=>path[(path.LastIndexOf('/')+1)..];
    private static string Child(string parent,string name)=>parent.Length==0?name:parent+"/"+name;
    private static void RequireCreate(UploadPrecondition condition)
    {if(condition.ExpectedVersion is not null)throw new NotSupportedException("群晖首版不支持可靠的条件覆盖，请另存副本。");}
    /// <summary>用户内容发送前验证目标不存在、父目录和新建能力；只读连接探测不调用此方法。</summary>
    public async Task PrepareUploadAsync(string path,UploadPrecondition condition,long contentLength,CancellationToken token=default)
    {
        path=NonRoot(path);ArgumentOutOfRangeException.ThrowIfNegative(contentLength);RequireCreate(condition);
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try{await PrepareCreateAsync(path,token).ConfigureAwait(false);}finally{_writes.Release();}
    }
    private async Task PrepareCreateAsync(string path,CancellationToken token)
    {
        if(await TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)throw new SourceConflictException("群晖目标已存在，请另存副本。");
        var parent=RelativePathRules.Parent(path);if(!(await GetMetadataAsync(parent,token).ConfigureAwait(false)).IsDirectory)throw new SourceConflictException("群晖上传父路径不是目录。");
        await VerifyCreateAsync(parent,token).ConfigureAwait(false);
    }
    private async Task VerifyCreateAsync(string parent,CancellationToken token)
    {
        if(_verifiedParents.Contains(parent))return;
        if(_blockedParents.Contains(parent))throw new NotSupportedException("本连接尚未确认群晖安全新建能力，请检查探测结果后重新连接。");
        _=Descriptor("SYNO.FileStation.Upload");
        var key=Child(parent,".hybridmount-probe-"+Guid.NewGuid().ToString("N"));var bytes=RandomNumberGenerator.GetBytes(32);
        var expected=Convert.ToHexString(MD5.HashData(bytes));_probePaths.TryAdd(key,0);var owned=false;
        try
        {
            using(var content=new MemoryStream(bytes))await UploadFileAsync(key,content,null,token).ConfigureAwait(false);owned=true;
            var rejected=false;
            try{using var different=new MemoryStream(RandomNumberGenerator.GetBytes(33));await UploadFileAsync(key,different,null,token).ConfigureAwait(false);}
            catch(SourceConflictException){rejected=true;}
            if(!rejected||await Md5Async(key,token).ConfigureAwait(false)!=expected)
            {WriteCapabilities=WriteCapabilities with{CreateOnly=CapabilitySupport.Unsupported};throw new NotSupportedException("群晖未可靠拒绝同名上传，已停用新建。");}
            token.ThrowIfCancellationRequested();_verifiedParents.Add(parent);WriteCapabilities=WriteCapabilities with{CreateOnly=CapabilitySupport.Supported};
        }
        catch{_blockedParents.Add(parent);throw;}
        finally
        {
            if(owned)await CleanupProbeAsync(key,expected).ConfigureAwait(false);
            else Warn("群晖新建探测未完成；仅需核对精确探测路径："+key);
        }
    }
    private async Task CleanupProbeAsync(string path,string expected)
    {
        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            if(await Md5Async(path,cleanup.Token).ConfigureAwait(false)!=expected){Warn("群晖探测内容变化，保留精确路径："+path);return;}
            var started=await ApiAsync("SYNO.FileStation.Delete","start",new(){["path"]=Paths(RemotePath(path)),["recursive"]="false"},true,cleanup.Token).ConfigureAwait(false);
            var id=started.GetProperty("taskid").GetString()??throw new IOException("群晖探测清理缺少任务标识。");
            while(true)
            {
                var status=await ApiAsync("SYNO.FileStation.Delete","status",new(){["taskid"]=id},false,cleanup.Token).ConfigureAwait(false);
                if(status.GetProperty("finished").GetBoolean())break;
                await Task.Delay(100,cleanup.Token).ConfigureAwait(false);
            }
            try{await GetRawMetadataAsync(path,cleanup.Token).ConfigureAwait(false);throw new IOException("群晖探测项仍存在。");}
            catch(FileNotFoundException){_probePaths.TryRemove(path,out _);}
        }
        catch(Exception error)when(error is IOException or OperationCanceledException or NotSupportedException){Warn("群晖探测清理未确认，保留精确路径："+path);}
    }
    /// <summary>expectedVersion 非空直接拒绝；空值仅创建，内容须为可定位的不可变快照。</summary>
    public Task<SourceEntry> UploadAsync(string path,Stream content,string? expectedVersion,CancellationToken token=default)
        =>UploadWithPreconditionAsync(path,content,new(expectedVersion),null,token);
    /// <summary>新建不会删除或改名已有目标；回执不明交由宿主持久事务只读核对。</summary>
    public async Task<SourceEntry> UploadWithPreconditionAsync(string path,Stream content,UploadPrecondition condition,IProgress<UploadProgress>? progress=null,CancellationToken token=default)
    {
        path=NonRoot(path);RequireCreate(condition);ArgumentNullException.ThrowIfNull(content);
        if(!content.CanRead||!content.CanSeek)throw new NotSupportedException("群晖上传需要可定位的完整快照。");
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await PrepareCreateAsync(path,token).ConfigureAwait(false);
            var uploaded=await UploadFileAsync(path,content,progress,token).ConfigureAwait(false);
            PublishBaseline(new(path,uploaded.Entry.Version,uploaded.Sha256));return uploaded.Entry;
        }
        finally{_writes.Release();}
    }
    private sealed record Uploaded(SourceEntry Entry,string Sha256);
    private async Task<Uploaded> UploadFileAsync(string path,Stream content,IProgress<UploadProgress>? progress,CancellationToken token)
    {
        var sid=await EnsureSessionAsync(token).ConfigureAwait(false);var api="SYNO.FileStation.Upload";
        using var hash=new Md5ReadStream(content);using var counting=new UploadProgressStream(hash,progress);
        using var request=new HttpRequestMessage(HttpMethod.Post,ApiAddress(api));var form=new MultipartFormDataContent();request.Content=form;request.Headers.ExpectContinue=false;
        // 不发送 overwrite，要求服务器在同名时明确拒绝；父目录必须由独立事务创建。
        var fields=Fields(api,"upload",new(){["path"]=RemotePath(RelativePathRules.Parent(path)),["create_parents"]="false",["mtime"]=Number(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())},sid);
        foreach(var field in fields)form.Add(new StringContent(field.Value),field.Key);
        var expectedLength=content.Length-content.Position;
        var file=new StreamContent(new BorrowedStream(counting));file.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");file.Headers.ContentLength=expectedLength;form.Add(file,"file",Name(path));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromHours(24));
        string digest,md5;long length;
        try
        {
            using(var response=await SendAsync(request,true,deadline.Token).ConfigureAwait(false))
            {
                using var reply=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);reply.CancelAfter(TimeSpan.FromSeconds(120));
                await ParseResponseAsync(response,true,reply.Token).ConfigureAwait(false);
            }
            if(counting.BytesRead!=expectedLength)throw new IOException("群晖上传未完整读取快照内容。");
            digest=counting.Digest;md5=hash.Digest;length=counting.BytesRead;
        }
        catch(SynologyApiException error)when(error.Code is 106 or 107 or 119){throw AuthenticationFailure(error.Code);}
        catch(SynologyApiException error){throw ApiFailure(error.Code);}
        catch(Exception error)when(error is not(SourceConflictException or SourceConnectionException or SourceOutcomeUnknownException or SourceRequestRejectedException))
        {throw new SourceOutcomeUnknownException("群晖新建",new IOException("上传未取得可信回执，请先只读核对。"));}
        try
        {
            var entry=await FingerprintAsync(await GetRawMetadataAsync(path,token).ConfigureAwait(false),token).ConfigureAwait(false);
            if(entry.IsDirectory||entry.Size!=length||ContentMd5(entry)!=md5)throw new IOException("群晖上传后内容核对失败。");
            return new(entry,digest);
        }
        catch(Exception){throw new SourceOutcomeUnknownException("群晖上传后核对",new IOException("文件可能已上传，请先只读核对远端内容。"));}
    }
    /// <summary>仅在已存在的父目录内新建目录；不隐式创建祖先或覆盖同名对象。</summary>
    public async Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)
    {
        path=NonRoot(path);await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(await TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)throw new SourceConflictException("群晖目标已存在。");
            var parent=RelativePathRules.Parent(path);if(!(await GetMetadataAsync(parent,token).ConfigureAwait(false)).IsDirectory)throw new SourceConflictException("群晖父路径不是目录。");
            await ApiAsync("SYNO.FileStation.CreateFolder","create",new(){["folder_path"]=Paths(RemotePath(parent)),["name"]=Paths(Name(path)),["force_parent"]="false"},true,token).ConfigureAwait(false);
            try{var entry=await GetMetadataAsync(path,token).ConfigureAwait(false);if(!entry.IsDirectory)throw new IOException();return entry;}
            catch(Exception){throw new SourceOutcomeUnknownException("群晖创建目录后核对",new IOException("目录可能已创建，请只读核对。"));}
        }
        finally{_writes.Release();}
    }
    /// <summary>首版禁止删除用户对象，不发送请求。</summary>
    public Task DeleteAsync(string path,string expectedVersion,CancellationToken token=default)
    {token.ThrowIfCancellationRequested();throw new NotSupportedException("群晖首版不支持可靠的条件删除，请保留远端内容。");}
    /// <summary>首版禁止移动或改名用户对象，不发送请求。</summary>
    public Task<SourceEntry> MoveAsync(string path,string destination,string expectedVersion,CancellationToken token=default)
    {token.ThrowIfCancellationRequested();throw new NotSupportedException("群晖首版不支持可靠的条件移动，请另存副本。");}
    private sealed class Md5ReadStream(Stream inner):Stream
    {
        private readonly IncrementalHash _hash=IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        internal bool Completed {get;private set;}
        public string Digest=>Convert.ToHexString(_hash.GetHashAndReset());
        public override bool CanRead=>inner.CanRead;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] bytes,int offset,int count){var read=inner.Read(bytes,offset,count);if(count>0&&read==0)Completed=true;_hash.AppendData(bytes,offset,read);return read;}
        public override async ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken token=default){var read=await inner.ReadAsync(bytes,token).ConfigureAwait(false);if(bytes.Length>0&&read==0)Completed=true;_hash.AppendData(bytes.Span[..read]);return read;}
        public override Task<int> ReadAsync(byte[] bytes,int offset,int count,CancellationToken token)=>ReadAsync(bytes.AsMemory(offset,count),token).AsTask();
        protected override void Dispose(bool disposing){if(disposing)_hash.Dispose();base.Dispose(disposing);}
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] bytes,int offset,int count)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();
    }
}
