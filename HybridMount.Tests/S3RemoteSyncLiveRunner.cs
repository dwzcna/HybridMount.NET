using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HybridMount.Common;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.Win32;
namespace HybridMount.Tests;

/// <summary>显式真实 S3/R2 对账验证；只读原配置，唯一远端文件与独立本地根，不创建测试目录或队列。</summary>
public static class S3RemoteSyncLiveRunner
{
    public static async Task<string> RunOriginalAsync(string localRoot,string area,CancellationToken token=default,bool capacityValidation=false)
    {
        MountDefinition original;
        using(var root=Registry.CurrentUser.OpenSubKey(@"Software\HybridMount.NET\Mounts\Unpackaged"))
        {
            if(root is null)throw new IOException("原非打包挂载配置不存在。");
            original=root.GetSubKeyNames().Select(name=>{using var key=root.OpenSubKey(name);return key?.GetValue("Definition") is string text?JsonSerializer.Deserialize<MountDefinition>(text):null;})
                .Single(x=>x?.Source?.Kind==FileSourceKind.S3&&x.LocalPath.Equals(localRoot,StringComparison.OrdinalIgnoreCase))!;
        }
        var options=original.Source!;var endpoint=FileSourceFactory.Endpoint(options);var data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HybridMount.NET","Mounting");
        if(endpoint.Host=="10.0.0.20")throw new IOException("已取消该 NAS 的真实测试。");
        var keys=options.UseDefaultCredentials?new SourceCredentials():new CredentialStore(data).Load(original.Id);
        area=Path.GetFullPath(area);var validation=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"artifacts","validation"));
        if(!MountPathGuard.IsUnder(validation,area)||Directory.Exists(area))throw new IOException("真实验证必须使用 validation 下新目录。");
        Directory.CreateDirectory(area);var run=Guid.NewGuid().ToString("N");var name=(capacityValidation?"hm009-":"hm008-")+run+".bin";var local=Path.Combine(area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\RealS3-"+run;var repository=new MountRepository(Path.Combine(area,"data"),registry);await using var logger=new LoggerService(Path.Combine(area,"logs.db"));
        var manager=new MountManager(repository,logger,new ProjectionFactory(name));await using var remote=new S3FileSource(options,keys,Path.Combine(area,"independent-cache"));
        using var raw=CreateClient(options,keys);var objectKey=(options.Prefix.Trim('/').Length==0?"":options.Prefix.Trim('/')+"/")+name;
        var payloads=new List<byte[]>();var cases=new List<object>();var cleanup=new List<object>();Guid? id=null;var phase="Preparing";string? failure=null;var report=Path.Combine(area,"report.json");
        void Save()=>File.WriteAllText(report,JsonSerializer.Serialize(new{Version=typeof(S3RemoteSyncLiveRunner).Assembly.GetName().Version!.ToString(),Endpoint=endpoint.AbsoluteUri,Bucket=options.Bucket,File=name,LocalRoot=local,MountId=id,Phase=phase,Failure=failure,Cases=cases,Cleanup=cleanup},new JsonSerializerOptions{WriteIndented=true}));
        try
        {
            Save();await remote.ProbeAsync(token);Require(await remote.TryGetMetadataAsync(name,token) is null,"唯一远端名称已经存在，拒绝接管。");
            var mount=await manager.AddAsync("008 独立 R2/S3 对账验证",local,false,token,options,keys);id=mount.Id;await manager.SetRemoteDetectionAsync(mount.Id,true,60,30,token);
            phase="FilesystemUpload";var first=Encoding.UTF8.GetBytes("HybridMount 009 filesystem "+run);payloads.Add(first);Save();
            if(capacityValidation)
            {
                var usage=await remote.GetRemoteUsageAsync(token);Require(usage.IsComplete&&usage.UsedBytes>0,"真实容量统计为空或不完整，保留现场待核对。");
                await manager.SetCapacityAsync(mount.Id,false,checked(usage.UsedBytes+first.Length-1),token);
                await File.WriteAllBytesAsync(Path.Combine(local,name),first,token);
                await Until(()=>repository.Uploads(mount.Id).Any(x=>x.LocalPath==name&&x.State=="WaitingCapacity"),token);
                Require(await remote.TryGetMetadataAsync(name,token) is null,"超限文件仍被写入远端。");
                cases.Add(new{Name="Capacity denied before remote write",State="Passed",Scope=usage.Scope,UsedBytes=usage.UsedBytes});Save();
                await manager.SetCapacityAsync(mount.Id,false,checked(usage.UsedBytes+first.Length),token);
            }
            else await File.WriteAllBytesAsync(Path.Combine(local,name),first,token);
            await Until(()=>repository.Uploads(mount.Id).Any(x=>x.LocalPath==name&&x.State=="Completed"),token);
            var metadata=await remote.GetMetadataAsync(name,token);Require(Digest(await Read(remote,metadata,token))==Digest(first),"真实文件系统上传读回摘要不符。");cases.Add(new{Name="Filesystem automatic upload SHA256",State="Passed"});
            if(capacityValidation){cases.Add(new{Name="Exact capacity limit filesystem upload",State="Passed"});await manager.SetCapacityAsync(mount.Id,false,null,token);}
            Require((await WebDavTests.Finished(manager,manager.Queue(mount.Id,"keepOnDevice",[Path.Combine(local,name)]))).State=="Completed","固定失败。");var identity=LocalFileState.Read(Path.Combine(local,name)).Identity;
            phase="AutomaticPinnedUpdate";var second=Enumerable.Range(0,65573).Select(x=>(byte)(x*13)).ToArray();payloads.Add(second);Save();
            await remote.UploadWithPreconditionAsync(name,new MemoryStream(second),new(metadata.Version,Digest(first)),token:token);
            var listCheck=await raw.ListObjectsV2Async(new(){BucketName=options.Bucket,Prefix=objectKey,MaxKeys=1},token);var headCheck=await raw.GetObjectMetadataAsync(new(){BucketName=options.Bucket,Key=objectKey},token);
            cases.Add(new{Name="Safe list/HEAD timestamp comparison",ListUtc=listCheck.S3Objects?.FirstOrDefault()?.LastModified,HeadUtc=headCheck.LastModified,SameETag=listCheck.S3Objects?.FirstOrDefault()?.ETag==headCheck.ETag,SameLength=listCheck.S3Objects?.FirstOrDefault()?.Size==headCheck.ContentLength});Save();
            await Until(()=>repository.AllRemotePending(mount.Id).Any(x=>x.Path==name&&x.Entry?.Size==second.Length&&x.State=="Completed"),token);
            Require(Digest(await File.ReadAllBytesAsync(Path.Combine(local,name),token))==Digest(second),"自动固定更新摘要不符。");Require(LocalFileState.Read(Path.Combine(local,name)).Identity==identity&&CloudFilesNative.Inspect(Path.Combine(local,name))!.Value.Info.PinState==1,"固定更新身份或状态变化。");cases.Add(new{Name="Automatic full reconciliation pinned update",State="Passed"});
            phase="RestartAndZeroByte";Save();await manager.DisposeAsync();manager=new MountManager(repository,logger,new ProjectionFactory(name));await manager.ConnectAsync(mount.Id,token);
            metadata=await remote.GetMetadataAsync(name,token);payloads.Add([]);await remote.UploadWithPreconditionAsync(name,new MemoryStream([]),new(metadata.Version,Digest(second)),token:token);
            await Until(()=>repository.AllRemotePending(mount.Id).Any(x=>x.Path==name&&x.Entry?.Size==0&&x.State=="Completed"),token);Require(new FileInfo(Path.Combine(local,name)).Length==0,"重启后零字节固定更新失败。");cases.Add(new{Name="Restart and zero-byte native update",State="Passed"});
            phase="RemoteDelete";Save();var head=await raw.GetObjectMetadataAsync(new(){BucketName=options.Bucket,Key=objectKey},token);Require(head.ContentLength==0,"远端删除前内容已变化。");
            await raw.DeleteObjectAsync(new(){BucketName=options.Bucket,Key=objectKey,IfMatch=head.ETag},token);await Until(()=>!File.Exists(Path.Combine(local,name)),token);await manager.ScanUploadChangesAsync(mount.Id,token);await Task.Delay(2200,token);
            Require(await remote.TryGetMetadataAsync(name,token) is null,"远端删除项被复活。");cases.Add(new{Name="Automatic confirmed remote deletion no resurrection",State="Passed"});phase="Completed";Save();
        }
        catch(Exception e){phase="Failed";failure=e is IOException or TimeoutException?e.Message:e.GetType().Name;cases.Add(new{Name="Failure evidence",Detection=manager.Mounts.FirstOrDefault()?.Detection,Pending= id is {} mounted?repository.AllRemotePending(mounted).Select(x=>new{x.State,x.Error,Size=x.Entry?.Size}).ToArray():null});Save();}
        finally
        {
            if(id is {} mount){try{await manager.DisconnectAsync(mount);}catch(Exception e){cleanup.Add(new{Scope="Disconnect",State="Failed",Error=e.GetType().Name});}}
            try
            {
                var entry=await remote.TryGetMetadataAsync(name,CancellationToken.None);
                if(entry is not null)
                {
                    var bytes=await Read(remote,entry,CancellationToken.None);Require(id is not null&&payloads.Any(x=>Digest(x)==Digest(bytes)),"远端名称出现非本轮内容，保留现场。");
                    var head=await raw.GetObjectMetadataAsync(new(){BucketName=options.Bucket,Key=objectKey});await raw.DeleteObjectAsync(new(){BucketName=options.Bucket,Key=objectKey,IfMatch=head.ETag});
                }
                cleanup.Add(new{Scope="ExactRemoteFile",State="Completed"});
            }
            catch(Exception e){cleanup.Add(new{Scope="ExactRemoteFile",State="Failed",Error=e is IOException?e.Message:e.GetType().Name});}
            if(id is {} owned)
            {
                var result=await WebDavTests.Finished(manager,manager.Queue(owned,"clearAndRemove"));cleanup.Add(new{Scope="LocalRoot",State=result.State,Error=result.Error});if(result.State=="Completed")Registry.CurrentUser.DeleteSubKeyTree(registry,false);
            }
            await manager.DisposeAsync();Save();
        }
        return report;
    }
    private static void Require(bool value,string message){if(!value)throw new IOException(message);}
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task Until(Func<bool> check,CancellationToken token)
    {var end=DateTimeOffset.UtcNow.AddSeconds(150);while(!check()){token.ThrowIfCancellationRequested();if(DateTimeOffset.UtcNow>end)throw new TimeoutException("真实 S3 对账阶段超时。");await Task.Delay(150,token);}}
    private static async Task<byte[]> Read(IFileSource source,SourceEntry entry,CancellationToken token)
    {
        if(entry.Size>1024*1024)throw new IOException("唯一测试文件大小异常，保留内容。");var bytes=new byte[(int)entry.Size];var offset=0;
        while(offset<bytes.Length){var count=await source.ReadRangeAsync(entry,offset,bytes.AsMemory(offset),token);if(count==0)throw new EndOfStreamException();offset+=count;}return bytes;
    }
    private sealed class HttpFactory:Amazon.Runtime.HttpClientFactory
    {public override HttpClient CreateHttpClient(IClientConfig config)=>new(new SocketsHttpHandler{AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(15)}){Timeout=TimeSpan.FromSeconds(45)};}
    private static IAmazonS3 CreateClient(FileSourceConfiguration options,SourceCredentials keys)
    {
        var config=new AmazonS3Config{ServiceURL=options.Endpoint.TrimEnd('/'),AuthenticationRegion=options.Region,ForcePathStyle=options.PathStyle,UseHttp=options.AllowHttp,MaxErrorRetry=0,DisableLogging=true,HttpClientFactory=new HttpFactory(),CacheHttpClient=false,
            RequestChecksumCalculation=RequestChecksumCalculation.WHEN_REQUIRED,ResponseChecksumValidation=ResponseChecksumValidation.WHEN_REQUIRED};
        if(options.UseDefaultCredentials)return new AmazonS3Client(config);
        AWSCredentials credentials=keys.SessionToken.Length==0?new BasicAWSCredentials(keys.AccessKey,keys.SecretKey):new SessionAWSCredentials(keys.AccessKey,keys.SecretKey,keys.SessionToken);return new AmazonS3Client(credentials,config);
    }
    private sealed class ProjectionFactory(string name):IFileSourceFactory
    {public async Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)=>new Projection((S3FileSource)await new FileSourceFactory().CreateAsync(mount,repository,recovery,token),name);}
    private sealed class Projection(S3FileSource source,string name):IFileSource,IRemoteChangeSource,IRemoteSnapshotSource,IUploadDiagnosticsSource,IRemoteUsageSource
    {
        public FileSourceCapabilities Capabilities=>source.Capabilities;public SourceWriteCapabilities WriteCapabilities=>source.WriteCapabilities;
        public bool IsSameVersion(string a,string b)=>((IFileSource)source).IsSameVersion(a,b);
        public event Action<SourceContentBaseline>? ContentBaseline{add=>source.ContentBaseline+=value;remove=>source.ContentBaseline-=value;}
        public event Action<string>? WriteWarning{add=>source.WriteWarning+=value;remove=>source.WriteWarning-=value;}
        public async IAsyncEnumerable<SourceEntry> ListAsync(string path,[EnumeratorCancellation]CancellationToken token=default){await foreach(var entry in source.ListAsync(path,token))if(entry.Path==name)yield return entry;}
        public async Task<IReadOnlyList<SourceEntry>> ReadSnapshotAsync(IReadOnlyList<RemoteShadowEntry> prior,CancellationToken token)=>(await source.ReadSnapshotAsync(prior,token)).Where(x=>x.Path==name).ToArray();
        public Task<RemoteUsage> GetRemoteUsageAsync(CancellationToken token=default)=>source.GetRemoteUsageAsync(token);
        public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)=>source.GetMetadataAsync(path,token);
        public Task<SourceEntry?> TryGetMetadataAsync(string path,CancellationToken token=default)=>source.TryGetMetadataAsync(path,token);
        public ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> bytes,CancellationToken token=default)=>source.ReadRangeAsync(entry,offset,bytes,token);
        public Task<SourceEntry> UploadAsync(string path,Stream stream,string? version,CancellationToken token=default)=>source.UploadAsync(path,stream,version,token);
        public Task<SourceEntry> UploadWithPreconditionAsync(string path,Stream stream,UploadPrecondition condition,IProgress<UploadProgress>? progress=null,CancellationToken token=default)=>source.UploadWithPreconditionAsync(path,stream,condition,progress,token);
        public Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)=>source.CreateDirectoryAsync(path,token);
        public Task<SourceEntry> MoveAsync(string path,string target,string version,CancellationToken token=default)=>source.MoveAsync(path,target,version,token);
        public Task DeleteAsync(string path,string version,CancellationToken token=default)=>source.DeleteAsync(path,version,token);
        public Task<bool> SupportsCollectionSyncAsync(string path,CancellationToken token)=>source.SupportsCollectionSyncAsync(path,token);
        public Task<RemoteChangeBatch> GetCollectionChangesAsync(string path,string? cursor,CancellationToken token)=>source.GetCollectionChangesAsync(path,cursor,token);
        public SourceEntry WithObservation(SourceEntry entry,long observation)=>entry;public bool HasReliableVersion(SourceEntry entry)=>source.HasReliableVersion(entry);
        public void RestoreObservations(IEnumerable<RemoteShadowEntry> entries){}public ValueTask DisposeAsync()=>source.DisposeAsync();
    }
}
