using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace HybridMount.Models.Mounting.Synology;

public sealed partial class SynologyFileSource
{
    private static string NonRoot(string path)=>RelativePathRules.Normalize(path) is {Length:>0} normalized&&!IsStagingPath(normalized)?normalized:throw new ArgumentException("不能修改群晖挂载根或保留的临时上传路径。");
    private static string Name(string path)=>path[(path.LastIndexOf('/')+1)..];
    private static string Child(string parent,string name)=>parent.Length==0?name:parent+"/"+name;
    private async Task<SourceEntry?> ValidateUploadAsync(string path,UploadPrecondition condition,CancellationToken token)
    {
        var current=await TryGetMetadataAsync(path,token).ConfigureAwait(false);
        if(condition.ExpectedVersion is null?current is not null:current is null||current.IsDirectory||!IsSameVersion(condition.ExpectedVersion,current.Version))
            throw new SourceConflictException("群晖上传目标已存在或版本发生变化。");
        if(condition.BaselineSha256 is {} hash&&(hash.Length!=64||hash.Any(c=>!char.IsAsciiHexDigit(c))))throw new ArgumentException("原始内容 SHA-256 基线无效。");
        return current;
    }
    public async Task PrepareUploadAsync(string path,UploadPrecondition condition,long contentLength,CancellationToken token=default)
    {NonRoot(path);ArgumentOutOfRangeException.ThrowIfNegative(contentLength);await ValidateUploadAsync(path,condition,token).ConfigureAwait(false);}
    public Task<SourceEntry> UploadAsync(string path,Stream content,string? expectedVersion,CancellationToken token=default)
        =>UploadWithPreconditionAsync(path,content,new(expectedVersion),null,token);
    public async Task<SourceEntry> UploadWithPreconditionAsync(string path,Stream content,UploadPrecondition condition,IProgress<UploadProgress>? progress=null,CancellationToken token=default)
    {
        path=NonRoot(path);ArgumentNullException.ThrowIfNull(content);if(!content.CanRead)throw new ArgumentException("上传流不可读。");
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            WarnWeakWrite();var current=await ValidateUploadAsync(path,condition,token).ConfigureAwait(false);
            if(current is null)
            {
                var created=await UploadFileAsync(path,content,progress,token).ConfigureAwait(false);
                PublishBaseline(new(path,created.Entry.Version,created.Sha256));return created.Entry;
            }
            var before=await Md5Async(path,token).ConfigureAwait(false);
            if(condition.BaselineSha256 is {} baseline)
            {
                var observed=await DownloadSnapshotAsync(current,Stream.Null,token).ConfigureAwait(false);
                if(!observed.Equals(baseline,StringComparison.OrdinalIgnoreCase))throw new SourceConflictException("群晖目标内容与原始上传基线不一致。");
            }
            var temporary=Child(RelativePathRules.Parent(path),".hm-"+Guid.NewGuid().ToString("N")+".tmp");
            var staged=false;var conflictSaved=false;
            try
            {
                var uploaded=await UploadFileAsync(temporary,content,progress,token).ConfigureAwait(false);staged=true;
                var target=await TryGetMetadataAsync(path,token).ConfigureAwait(false);
                var matches=target is not null&&!target.IsDirectory&&IsSameVersion(current.Version,target.Version)&&await Md5Async(path,token).ConfigureAwait(false)==before;
                if(!matches)
                {
                    var conflict=await ConflictCopyPathAsync(path,token).ConfigureAwait(false);
                    await RenameAsync(temporary,conflict,token).ConfigureAwait(false);
                    conflictSaved=true;
                    throw new SourceConflictException("群晖目标发生变化；上传内容已保存为冲突副本："+conflict);
                }
                token.ThrowIfCancellationRequested();
                await DeleteTaskAsync(path,token).ConfigureAwait(false);
                await RenameAsync(temporary,path,token).ConfigureAwait(false);
                var receipt=await GetMetadataAsync(path,token).ConfigureAwait(false);
                if(receipt.Size!=uploaded.Entry.Size||await Md5Async(path,token).ConfigureAwait(false)!=uploaded.Md5)throw new IOException("群晖替换后内容核对失败。");
                PublishBaseline(new(path,receipt.Version,uploaded.Sha256));return receipt;
            }
            catch(SourceConflictException)when(conflictSaved){throw;}
            catch(SourceOutcomeUnknownException){throw;}
            catch(Exception)when(staged){throw new SourceOutcomeUnknownException("群晖替换",new IOException("临时上传已完成；请核对目标与临时文件："+temporary));}
        }
        finally{_writes.Release();}
    }
    private async Task<string> ConflictCopyPathAsync(string path,CancellationToken token)
    {
        var name=Name(path);var extension=Path.GetExtension(name);var stem=name[..(name.Length-extension.Length)];
        if(stem.Length==0){stem=name;extension="";}
        var timestamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",System.Globalization.CultureInfo.InvariantCulture);
        var parent=RelativePathRules.Parent(path);
        for(var index=1;;index=checked(index+1))
        {
            token.ThrowIfCancellationRequested();var suffix=index==1?"":"-"+Number(index);
            var candidate=Child(parent,stem+" (冲突副本 "+timestamp+")"+suffix+extension);
            if(await TryGetMetadataAsync(candidate,token).ConfigureAwait(false) is null)return candidate;
        }
    }
    private sealed record Uploaded(SourceEntry Entry,string Sha256,string Md5);
    private async Task<Uploaded> UploadFileAsync(string path,Stream content,IProgress<UploadProgress>? progress,CancellationToken token)
    {
        var position=content.CanSeek?content.Position:-1;
        for(var attempt=0;;attempt++)
        {
            var sid=await EnsureSessionAsync(token).ConfigureAwait(false);var api="SYNO.FileStation.Upload";string digest,md5;long length;
            try
            {
                using var hash=new Md5ReadStream(content);using var counting=new UploadProgressStream(hash,progress);
                using(var request=new HttpRequestMessage(HttpMethod.Post,ApiAddress(api)))
                {
                    var form=new MultipartFormDataContent();request.Content=form;request.Headers.ExpectContinue=false;
                    var fields=new Dictionary<string,string>{["api"]=api,["version"]=Number(Descriptor(api).Version),["method"]="upload",["_sid"]=sid,
                        ["path"]=RemotePath(RelativePathRules.Parent(path)),["create_parents"]="true",["overwrite"]="false",["mtime"]=Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds())};
                    // 待实测：overwrite=false 的拒绝覆盖是否具备原子性；当前仅声明弱写入能力。
                    foreach(var field in fields)form.Add(new StringContent(field.Value),field.Key);
                    var file=new StreamContent(new BorrowedStream(counting));file.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
                    if(position>=0)file.Headers.ContentLength=content.Length-position;
                    form.Add(file,"file",Name(path));
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromHours(24));
                    using var response=await SendAsync(request,true,deadline.Token).ConfigureAwait(false);
                    using var replyDeadline=CancellationTokenSource.CreateLinkedTokenSource(token);replyDeadline.CancelAfter(TimeSpan.FromSeconds(120));
                    var data=await ParseResponseAsync(response,true,replyDeadline.Token).ConfigureAwait(false);CheckTaskErrors(data);
                }
                if(!hash.Completed)throw new IOException("群晖上传未完整读取快照内容。");
                digest=counting.Digest;md5=hash.Digest;length=counting.BytesRead;
            }
            catch(SynologyApiException error)when(error.Code is 106 or 107 or 119)
            {
                if(attempt!=0)throw new SynologyAuthenticationException(error.Code);
                await EnsureSessionAsync(token,sid).ConfigureAwait(false);
                if(position<0)throw new SynologyAuthenticationException(error.Code);
                content.Position=position;continue;
            }
            catch(Exception error)when(error is not (SynologyApiException or SynologyAuthenticationException or SynologyCertificateException or SourceOutcomeUnknownException or SourceRequestRejectedException))
            {throw new SourceOutcomeUnknownException("群晖上传",new IOException("上传未取得可信回执。"));}
            try
            {
                var entry=await GetRawMetadataAsync(path,token).ConfigureAwait(false);
                if(position>=0&&length!=content.Length-position||entry.IsDirectory||entry.Size!=length||await Md5Async(path,token).ConfigureAwait(false)!=md5)throw new IOException("群晖上传后内容核对失败。");
                return new(entry,digest,md5);
            }
            catch(Exception){throw new SourceOutcomeUnknownException("群晖上传后核对",new IOException("文件可能已上传，请核对远端内容。"));}
        }
    }
    private static void CheckTaskErrors(JsonElement data)
    {
        if(data.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("code",out var code)&&code.GetInt32()!=0)throw new SynologyApiException(code.GetInt32());
        if(data.TryGetProperty("errors",out var errors)&&errors.ValueKind==JsonValueKind.Array&&errors.GetArrayLength()>0)
            throw new SynologyApiException(errors[0].GetProperty("code").GetInt32());
    }
    private async Task RunTaskAsync(string api,Dictionary<string,string> fields,CancellationToken token)
    {
        var started=await ApiAsync(api,"start",fields,true,token).ConfigureAwait(false);
        try
        {
            var id=started.GetProperty("taskid").GetString()!;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(30));
            while(true)
            {
                var status=await ApiAsync(api,"status",new(){["taskid"]=id},false,deadline.Token).ConfigureAwait(false);
                CheckTaskErrors(status);if(status.GetProperty("finished").GetBoolean())return;
                await Task.Delay(TimeSpan.FromMilliseconds(500),deadline.Token).ConfigureAwait(false);
            }
        }
        catch(Exception){throw new SourceOutcomeUnknownException("群晖后台任务",new IOException("任务已提交但未取得完整成功回执；请核对源和目标。"));}
    }
    private Task DeleteTaskAsync(string path,CancellationToken token)
        =>RunTaskAsync("SYNO.FileStation.Delete",new(){["path"]=Paths(RemotePath(path)),["recursive"]="false"},token);
    private async Task RenameAsync(string path,string destination,CancellationToken token)
    {
        if(RelativePathRules.Parent(path)!=RelativePathRules.Parent(destination))throw new ArgumentException("群晖 Rename 只能在同目录内进行。");
        if(await TryGetMetadataAsync(destination,token).ConfigureAwait(false) is not null)throw new SourceConflictException("群晖重命名目标已存在。");
        // 待实测：Rename 的目标存在行为、原子性及返回路径；禁止依赖它提供条件替换保证。
        await ApiAsync("SYNO.FileStation.Rename","rename",new(){["path"]=Paths(RemotePath(path)),["name"]=Paths(Name(destination)),["overwrite"]="false"},true,token).ConfigureAwait(false);
    }
    public async Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)
    {
        path=NonRoot(path);await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(await TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)throw new SourceConflictException("群晖目标已存在。");
            await ApiAsync("SYNO.FileStation.CreateFolder","create",new(){["folder_path"]=Paths(RemotePath(RelativePathRules.Parent(path))),["name"]=Paths(Name(path)),["force_parent"]="false"},true,token).ConfigureAwait(false);
            try{var entry=await GetMetadataAsync(path,token).ConfigureAwait(false);if(!entry.IsDirectory)throw new IOException();return entry;}
            catch(Exception){throw new SourceOutcomeUnknownException("群晖创建目录后核对",new IOException("目录创建后无法取得元数据。"));}
        }
        finally{_writes.Release();}
    }
    public async Task DeleteAsync(string path,string expectedVersion,CancellationToken token=default)
    {
        path=NonRoot(path);await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            WarnWeakWrite();var current=await GetMetadataAsync(path,token).ConfigureAwait(false);
            if(!IsSameVersion(expectedVersion,current.Version))throw new SourceConflictException("群晖删除目标版本已变化。");
            if(current.IsDirectory)await foreach(var _ in ListAsync(path,token).ConfigureAwait(false))throw new SourceConflictException("仅允许删除空群晖目录。");
            await DeleteTaskAsync(path,token).ConfigureAwait(false);
            try{if(await TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)throw new IOException();}
            catch(Exception){throw new SourceOutcomeUnknownException("群晖删除后核对",new IOException("无法确认目标已删除。"));}
        }
        finally{_writes.Release();}
    }
    public async Task<SourceEntry> MoveAsync(string path,string destination,string expectedVersion,CancellationToken token=default)
    {
        path=NonRoot(path);destination=NonRoot(destination);
        if(RelativePathRules.Contains(path,destination))throw new ArgumentException("不能移入自身。");
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            WarnWeakWrite();var current=await GetMetadataAsync(path,token).ConfigureAwait(false);
            if(!IsSameVersion(expectedVersion,current.Version))throw new SourceConflictException("群晖移动目标版本已变化。");
            if(await TryGetMetadataAsync(destination,token).ConfigureAwait(false) is not null)throw new SourceConflictException("群晖移动目标已存在。");
            if(RelativePathRules.Parent(path)==RelativePathRules.Parent(destination))await RenameAsync(path,destination,token).ConfigureAwait(false);
            else
            {
                var intermediate=Child(RelativePathRules.Parent(destination),Name(path));
                if(await TryGetMetadataAsync(intermediate,token).ConfigureAwait(false) is not null)throw new SourceConflictException("群晖移动目录中已有同名文件。");
                await RunTaskAsync("SYNO.FileStation.CopyMove",new(){["path"]=Paths(RemotePath(path)),["dest_folder_path"]=RemotePath(RelativePathRules.Parent(destination)),["remove_src"]="true",["overwrite"]="false"},token).ConfigureAwait(false);
                try{if(intermediate!=destination)await RenameAsync(intermediate,destination,token).ConfigureAwait(false);}
                catch(Exception){throw new SourceOutcomeUnknownException("群晖移动后重命名",new IOException("文件可能已移至："+intermediate));}
            }
            try
            {
                var result=await GetMetadataAsync(destination,token).ConfigureAwait(false);
                if(result.IsDirectory!=current.IsDirectory||result.Size!=current.Size||await TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)throw new IOException();
                return result;
            }
            catch(Exception){throw new SourceOutcomeUnknownException("群晖移动后核对",new IOException("请核对移动源和目标。"));}
        }
        finally{_writes.Release();}
    }
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
