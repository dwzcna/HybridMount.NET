using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridMount.Common;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using WebDAVClient;
using WebDAVClient.Protocol;
namespace HybridMount.Tests;

/// <summary>显式真实服务入口；只在已有共享内操作本轮唯一文件，普通自动化不调用，不创建远端测试目录。</summary>
public static class RemoteSyncLiveRunner
{
    /// <summary>只读原挂载定义及凭据，使用独立本地根；正式根与原配置不改绑。</summary>
    public static Task<string> RunOriginalAsync(string localRoot,string area,CancellationToken token=default,bool capacityValidation=false)
    {
        var data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HybridMount.NET","Mounting");
        MountDefinition original;
        using(var registry=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HybridMount.NET\Mounts\Unpackaged"))
        {
            if(registry is null)throw new IOException("原非打包挂载配置不存在。");
            original=registry.GetSubKeyNames().Select(name=>{using var key=registry.OpenSubKey(name);return key?.GetValue("Definition") is string json?JsonSerializer.Deserialize<MountDefinition>(json):null;})
                .Single(x=>x?.Source?.Kind==FileSourceKind.WebDav&&x.LocalPath.Equals(localRoot,StringComparison.OrdinalIgnoreCase))!;
        }
        if(new Uri(original.Source!.Endpoint).Host=="10.0.0.20")throw new IOException("已取消该 NAS 的真实测试。");
        var options=original.Source!;
        if(new Uri(options.Endpoint).Host=="dav.jianguoyun.com"&&new Uri(options.Endpoint).AbsolutePath.TrimEnd('/')=="/dav")
            options=options with{Endpoint=options.Endpoint.TrimEnd('/')+"/"+Uri.EscapeDataString("我的坚果云")+"/"};
        return RunAsync(options,new CredentialStore(data).Load(original.Id),area,token,capacityValidation);
    }
    private sealed class Authentication(SourceCredentials credentials) : DelegatingHandler(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(15)})
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName+":"+credentials.Password)));return base.SendAsync(request,token);}
    }
    private sealed class WireErrors : DelegatingHandler
    {
        public WireErrors():base(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(15)}){}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {try{return await base.SendAsync(request,token);}catch(HttpRequestException e){throw new IOException($"DAV {request.Method}: transport={e.HttpRequestError}, inner={e.InnerException?.GetType().Name}, code={e.InnerException?.HResult:X8}。",e);}}
    }
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(bool value,string reason){if(!value)throw new IOException(reason);}
    private static async Task WaitAsync(Func<bool> condition,CancellationToken token,int seconds=150)
    {var end=DateTimeOffset.UtcNow.AddSeconds(seconds);while(!condition()){token.ThrowIfCancellationRequested();if(DateTimeOffset.UtcNow>end)throw new TimeoutException("真实服务状态未在预算内达到预期。");await Task.Delay(100,token);}}
    private static async Task<string> RunAsync(FileSourceConfiguration options,SourceCredentials credentials,string area,CancellationToken token,bool capacityValidation)
    {
        area=Path.GetFullPath(area);var validation=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"artifacts","validation"));
        if(!MountPathGuard.IsUnder(validation,area)||Directory.Exists(area))throw new IOException("验证目录必须是仓库 validation 下的新目录。");
        Directory.CreateDirectory(area);var run=Guid.NewGuid().ToString("N");var name=(capacityValidation?"hm009-":"hm007-")+run+".bin";var local=Path.Combine(area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\Remote007-"+run;var repository=new MountRepository(Path.Combine(area,"data"),registry);
        await using var logger=new LoggerService(Path.Combine(area,"logs.db"));
        var payloads=new List<byte[]>();var cases=new List<object>();var cleanup=new List<object>();var phase="Preparing";Guid? mountId=null;string? failure=null;
        var report=Path.Combine(area,"report.json");
        void Save()=>File.WriteAllText(report,JsonSerializer.Serialize(new{Version=typeof(RemoteSyncLiveRunner).Assembly.GetName().Version!.ToString(),Endpoint=options.Endpoint,File=name,LocalRoot=local,MountId=mountId,Phase=phase,Failure=failure,Cases=cases,Cleanup=cleanup},new JsonSerializerOptions{WriteIndented=true}));
        await using var remote=new WebDavFileSource(options,credentials,new WholeFileCache(Path.Combine(area,"verify-cache")),new WireErrors());
        using var http=new HttpClient(new Authentication(credentials)){Timeout=TimeSpan.FromSeconds(45)};
        using var independent=new DavClient(FileSourceFactory.Endpoint(options),http);var davPath=DavPath.Parse(Uri.EscapeDataString(name));
        var factory=new SingleFileFactory(name);var manager=new MountManager(repository,logger,factory);var created=false;
        async Task ExternalPut(byte[] bytes)
        {
            // 精确本轮名称、原先不存在且每次覆盖前核对已记录内容，避免接管外部对象。
            var existing=await remote.TryGetMetadataAsync(name,token);
            if(existing is not null){var actual=await ReadRemote(remote,existing,token);Require(payloads.Any(x=>Digest(x)==Digest(actual)),"远端唯一测试文件出现外部内容，停止覆盖。");}
            payloads.Add(bytes);Save();using var content=new MemoryStream(bytes,false);
            var result=await independent.UploadAsync(davPath,content,cancellationToken:token);Require((int)result.StatusCode is 200 or 201 or 204,"独立 DavClient 写入失败。");created=true;
        }
        try
        {
            phase="Probe";Save();await remote.ProbeAsync(token);Require(await remote.TryGetMetadataAsync(name,token) is null,"本轮名称已存在。");
            var sync=await remote.SupportsCollectionSyncAsync("",token);var reportVerified=false;
            if(sync){try{var batch=await remote.GetCollectionChangesAsync("",null,token);reportVerified=batch.NextToken.Length>0;}catch(RemoteSyncException e)when(e.Unsupported){sync=false;}}
            cases.Add(new{Name="Real capability discovery",Advertised=sync,ValidReport=reportVerified,Mode=reportVerified?"RFC6578":"Full reconciliation"});
            var mount=await manager.AddAsync("007 独立真实同步验证",local,false,token,options,credentials);mountId=mount.Id;
            // 缩短测试周期仍走正式调度路径；实际默认保持 60/300 秒。
            await manager.SetRemoteDetectionAsync(mount.Id,true,10,30,token);Save();
            phase="FilesystemUpload";var first=Encoding.UTF8.GetBytes("HybridMount 009 filesystem "+run);payloads.Add(first);Save();
            if(capacityValidation)
            {
                var usage=await remote.GetRemoteUsageAsync(token);Require(usage.IsComplete&&usage.UsedBytes>0,"真实容量统计为空或不完整，保留现场待核对。");
                await manager.SetCapacityAsync(mount.Id,false,checked(usage.UsedBytes+first.Length-1),token);
                await File.WriteAllBytesAsync(Path.Combine(local,name),first,token);
                await WaitAsync(()=>repository.Uploads(mount.Id).Any(x=>x.LocalPath==name&&x.State=="WaitingCapacity"),token);
                Require(await remote.TryGetMetadataAsync(name,token) is null,"超限文件仍被写入远端。");
                cases.Add(new{Name="Capacity denied before remote write",State="Passed",Scope=usage.Scope,UsedBytes=usage.UsedBytes});Save();
                await manager.SetCapacityAsync(mount.Id,false,checked(usage.UsedBytes+first.Length),token);
            }
            else await File.WriteAllBytesAsync(Path.Combine(local,name),first,token);
            await WaitAsync(()=>repository.Uploads(mount.Id).Any(x=>x.LocalPath==name&&x.State=="Completed"),token);
            var metadata=await remote.GetMetadataAsync(name,token);Require(Digest(await ReadRemote(remote,metadata,token))==Digest(first),"自动上传摘要不符。");created=true;
            cases.Add(new{Name="Filesystem upload and independent SHA256",State="Passed"});
            if(capacityValidation){cases.Add(new{Name="Exact capacity limit filesystem upload",State="Passed"});await manager.SetCapacityAsync(mount.Id,false,null,token);}
            var pin=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"keepOnDevice",[Path.Combine(local,name)]));Require(pin.State=="Completed",pin.Error??"固定失败。");
            var identity=LocalFileState.Read(Path.Combine(local,name)).Identity;
            phase="AutomaticPinnedUpdate";Save();var second=Enumerable.Range(0,65573).Select(x=>(byte)(x*11)).ToArray();await ExternalPut(second);
            await WaitAsync(()=>repository.AllRemotePending(mount.Id).Any(x=>x.Path==name&&x.State=="Completed"&&x.Entry?.Size==second.Length),token);
            Require(Digest(await File.ReadAllBytesAsync(Path.Combine(local,name),token))==Digest(second),"固定自动更新摘要不符。");
            Require(LocalFileState.Read(Path.Combine(local,name)).Identity==identity&&CloudFilesNative.Inspect(Path.Combine(local,name))!.Value.Info.PinState==1,"固定状态或占位身份变化。");
            cases.Add(new{Name="Automatic pinned update and identity",State="Passed",Bytes=second.Length});
            phase="RestartAndZeroByte";Save();await manager.DisposeAsync();manager=new MountManager(repository,logger,factory);await manager.ConnectAsync(mount.Id,token);
            await ExternalPut([]);await WaitAsync(()=>repository.AllRemotePending(mount.Id).Any(x=>x.Path==name&&x.State=="Completed"&&x.Entry?.Size==0),token);
            Require((await File.ReadAllBytesAsync(Path.Combine(local,name),token)).Length==0,"重启后零字节更新失败。");cases.Add(new{Name="Restart replay and zero-byte pinned update",State="Passed"});
            phase="RemoteDelete";Save();metadata=await remote.GetMetadataAsync(name,token);Require((await ReadRemote(remote,metadata,token)).Length==0,"删除前内容身份不符。");
            var deleted=await independent.DeleteAsync(davPath,cancellationToken:token);Require((int)deleted.StatusCode is 200 or 204 or 404,"独立精确删除失败。");
            await WaitAsync(()=>!File.Exists(Path.Combine(local,name)),token);await manager.ScanUploadChangesAsync(mount.Id,token);await Task.Delay(2200,token);
            Require(await remote.TryGetMetadataAsync(name,token) is null,"远端删除项被自动复活。");cases.Add(new{Name="Automatic remote delete and no resurrection",State="Passed"});
            phase="Completed";Save();
        }
        catch(Exception error){phase="Failed";failure=error is IOException or TimeoutException?error.Message:error.GetType().Name;Save();}
        finally
        {
            // 先停止所有调度，再清理当前唯一测试文件；任何外部内容均留存并报告。
            if(mountId is {} id){try{await manager.DisconnectAsync(id);}catch(Exception e){cleanup.Add(new{Scope="Disconnect",State="Failed",Error=e.GetType().Name});}}
            try
            {
                var entry=await remote.TryGetMetadataAsync(name,CancellationToken.None);
                if(entry is not null)
                {
                    var bytes=await ReadRemote(remote,entry,CancellationToken.None);Require((created||mountId is not null)&&payloads.Any(x=>Digest(x)==Digest(bytes)),"远端文件不在本轮已知内容清单，保留现场。");
                    var removed=await independent.DeleteAsync(davPath);Require((int)removed.StatusCode is 200 or 204 or 404,"本轮远端文件清理失败。");
                }
                cleanup.Add(new{Scope="ExactRemoteFile",State="Completed"});
            }
            catch(Exception e){cleanup.Add(new{Scope="ExactRemoteFile",State="Failed",Error=e.Message});}
            if(mountId is {} owned)
            {
                var result=await WebDavTests.Finished(manager,manager.Queue(owned,"clearAndRemove"));cleanup.Add(new{Scope="LocalSyncRoot",State=result.State,Error=result.Error});
                if(result.State=="Completed")Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
            }
            await manager.DisposeAsync();Save();
        }
        return report;
    }
    private static async Task<byte[]> ReadRemote(IFileSource source,SourceEntry entry,CancellationToken token)
    {
        if(entry.Size>1024*1024)throw new IOException("本轮小测试文件大小异常，拒绝读取与清理。");
        var bytes=new byte[(int)entry.Size];var offset=0;while(offset<bytes.Length){var read=await source.ReadRangeAsync(entry,offset,bytes.AsMemory(offset),token);if(read==0)throw new EndOfStreamException();offset+=read;}return bytes;
    }
    /// <summary>只把本轮唯一文件投影为本地根；实际发现和 REPORT 仍向真实共享发出，其他用户内容不入本地索引。</summary>
    private sealed class SingleFileFactory(string name) : IFileSourceFactory
    {
        public async Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)
        {
            // 保留正式认证处理器，补充安全的传输阶段分类，不记录授权头或服务正文。
            var source=new WebDavFileSource(mount.Source!,new CredentialStore(repository.DataDirectory).Load(mount.Id),new WholeFileCache(Path.Combine(repository.DataDirectory,"live-cache")),new WireErrors());
            source.AttachTemporaryJournal(mount,repository);await source.ProbeAsync(token);
            return new SingleFileSource(source,name);
        }
    }
    private sealed class SingleFileSource(WebDavFileSource source,string name) : IFileSource,IRemoteChangeSource,IUploadDiagnosticsSource,IRemoteUsageSource
    {
        public FileSourceCapabilities Capabilities=>source.Capabilities;
        public SourceWriteCapabilities WriteCapabilities=>source.WriteCapabilities;
        public bool IsSameVersion(string left,string right)=>source.IsSameVersion(left,right);
        public event Action<SourceContentBaseline>? ContentBaseline{add=>source.ContentBaseline+=value;remove=>source.ContentBaseline-=value;}
        public event Action<string>? WriteWarning{add=>source.WriteWarning+=value;remove=>source.WriteWarning-=value;}
        public Task ProbeAsync(CancellationToken token=default)=>source.ProbeAsync(token);
        public async IAsyncEnumerable<SourceEntry> ListAsync(string path,[EnumeratorCancellation]CancellationToken token=default)
        {await foreach(var item in source.ListAsync(path,token))if(item.Path==name)yield return item;}
        public Task<RemoteUsage> GetRemoteUsageAsync(CancellationToken token=default)=>source.GetRemoteUsageAsync(token);
        public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)=>source.GetMetadataAsync(path,token);
        public Task<SourceEntry?> TryGetMetadataAsync(string path,CancellationToken token=default)=>source.TryGetMetadataAsync(path,token);
        public ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)=>source.ReadRangeAsync(entry,offset,buffer,token);
        public Task<SourceEntry> UploadAsync(string path,Stream content,string? version,CancellationToken token=default)=>source.UploadAsync(path,content,version,token);
        public Task<SourceEntry> UploadWithPreconditionAsync(string path,Stream content,UploadPrecondition condition,IProgress<UploadProgress>? progress=null,CancellationToken token=default)=>source.UploadWithPreconditionAsync(path,content,condition,progress,token);
        public Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)=>source.CreateDirectoryAsync(path,token);
        public Task<SourceEntry> MoveAsync(string path,string target,string version,CancellationToken token=default)=>source.MoveAsync(path,target,version,token);
        public Task DeleteAsync(string path,string version,CancellationToken token=default)=>source.DeleteAsync(path,version,token);
        public Task<bool> SupportsCollectionSyncAsync(string path,CancellationToken token)=>source.SupportsCollectionSyncAsync(path,token);
        public async Task<RemoteChangeBatch> GetCollectionChangesAsync(string path,string? cursor,CancellationToken token)
        {var result=await source.GetCollectionChangesAsync(path,cursor,token);return result with{Entries=result.Entries.Where(x=>x.Path==name).ToArray(),Deleted=result.Deleted.Where(x=>x==name).ToArray()};}
        public SourceEntry WithObservation(SourceEntry entry,long observation)=>source.WithObservation(entry,observation);
        public bool IsSameVersionHint(string expected,string observed)=>source.IsSameVersionHint(expected,observed);
        public bool HasReliableVersion(SourceEntry entry)=>source.HasReliableVersion(entry);
        public void RestoreObservations(IEnumerable<RemoteShadowEntry> entries)=>source.RestoreObservations(entries);
        public ValueTask DisposeAsync()=>source.DisposeAsync();
    }
}
