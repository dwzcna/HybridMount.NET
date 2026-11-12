using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>显式调用的真实 WebDAV 长时测试器；普通单元测试不会联网，凭据仅从当前用户 DPAPI 读取。</summary>
public static partial class LiveWebDavRunner
{
    /// <summary>在唯一远端集合和独立 NTFS 根测试 60–180 分钟，返回报告路径；不操作既有挂载或明文保存凭据。</summary>
    /// <param name="endpoint">已授权且可写的 WebDAV 共享根，不包含用户信息。</param>
    /// <param name="workDirectory">独立验证目录，包含 CredentialStore 保存的密文。</param>
    /// <param name="credentialId">当前用户密文凭据标识。</param>
    /// <param name="minutes">从远端测试集合建立成功开始计算的真实测试时长。</param>
    /// <param name="token">调用方停止令牌；停止后仍尝试清理精确拥有的测试数据。</param>
    public static Task<string> RunAsync(string endpoint,string workDirectory,Guid credentialId,int minutes=65,CancellationToken token=default)
        =>new Session(endpoint,workDirectory,credentialId,minutes).RunAsync(token);

    private sealed record Result(string Name,string State,long Milliseconds,string? Detail=null);
    private sealed class CapabilityLimitedException(string message):Exception(message);
    /// <summary>每轮拥有独立协议客户端、注册表、日志和远端名称；清理拒绝不在本轮清单中的对象。</summary>
    private sealed partial class Session
    {
        private readonly string _work,_registry,_remoteName,_endpoint;
        private readonly int _minutes;
        private readonly bool _extended,_focused;
        private readonly SourceCredentials _credentials;
        private readonly List<Result> _results=[];
        private readonly HashSet<string> _known=new(StringComparer.Ordinal);
        private readonly HashSet<string> _expectedRefreshConflicts=new(StringComparer.Ordinal);
        private readonly Stopwatch _elapsed=new();
        private readonly Guid _runId;
        private WebDavFileSource? _base,_source;
        private MountRepository? _repository;
        private LoggerService? _logger;
        private MountManager? _manager;
        private MountDefinition? _mount;
        private HttpClient? _raw;
        private bool _created,_updates;
        private int _round;
        private DateTimeOffset? _started,_ended;
        private string _phase="Preparing";
        public Session(string endpoint,string work,Guid id,int minutes,bool extended=false,bool focused=false)
        {
            if(minutes is <60 or >180)throw new ArgumentOutOfRangeException(nameof(minutes));
            _endpoint=FileSourceFactory.Endpoint(new(FileSourceKind.WebDav,endpoint,AllowHttp:new Uri(endpoint).Scheme=="http")).AbsoluteUri;
            _work=Path.GetFullPath(work);_runId=id;_minutes=minutes;_extended=extended;_focused=focused;Directory.CreateDirectory(_work);
            _credentials=new CredentialStore(_work).Load(id);if(_credentials.UserName.Length==0||_credentials.Password.Length==0)throw new IOException("缺少本次 DPAPI 测试凭据。");
            _registry=@"Software\HybridMount.NET.Tests\LiveDav-"+id.ToString("N");_remoteName="hm-validation-0053-"+id.ToString("N");
        }
        private string Local=>Path.Combine(_work,"mount");
        private string RootUri=>_endpoint+Uri.EscapeDataString(_remoteName)+"/";
        private static FileSourceConfiguration Options(string endpoint)=>new(FileSourceKind.WebDav,endpoint,AllowHttp:new Uri(endpoint).Scheme=="http");
        private WebDavFileSource Source(string endpoint,string name,HttpMessageHandler? handler=null)=>new(Options(endpoint),_credentials,new WholeFileCache(Path.Combine(_work,name)),handler);
        private void Track(string path)
        {
            path=RelativePaths.Normalize(path);_known.Add(path);
            for(var parent=RelativePaths.Parent(path);parent.Length!=0;parent=RelativePaths.Parent(parent))_known.Add(parent);
            Save();
        }
        private void Save()
        {
            var report=new{Version=typeof(MountManager).Assembly.GetName().Version!.ToString(),RunId=_runId,Endpoint=_endpoint,RemoteRoot=_remoteName,Phase=_phase,
                StartedUtc=_started,EndedUtc=_ended,ElapsedSeconds=_elapsed.Elapsed.TotalSeconds,RequestedMinutes=_minutes,Round=_round,Extended=_extended,Focused=_focused,IndependentDavChecks=_independent.Values.ToArray(),
                ObservedIssues=_results.Count(x=>x.State=="ObservedIssue"),Passed=_results.Count(x=>x.State=="Passed"),Limited=_results.Count(x=>x.State=="CapabilityLimited"),Failed=_results.Count(x=>x.State=="Failed"),
                Capabilities=_source?.Capabilities,WriteCapabilities=_source?.WriteCapabilities,ExpectedRefreshConflicts=_expectedRefreshConflicts.ToArray(),KnownPaths=_known.Order(StringComparer.Ordinal).ToArray(),Results=_results};
            var temporary=Path.Combine(_work,"live-report.json.tmp");File.WriteAllText(temporary,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            // 外部读取仪表文件可能产生短暂重命名竞争；有界等待，真实访问失败仍向外报告。
            var writeWait=Stopwatch.StartNew();
            while(true)
            {
                try{File.Move(temporary,Path.Combine(_work,"live-report.json"),true);break;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException&&writeWait.Elapsed<TimeSpan.FromSeconds(2)){Thread.Sleep(20);}
            }
        }
        private static string Failure(Exception error)=>error is UnauthorizedAccessException?error.ToString():error is IOException?error.Message:error.GetType().Name;
        private async Task Case(string name,Func<CancellationToken,Task> operation,CancellationToken stopping,TimeSpan? budget=null)
        {
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(stopping);limit.CancelAfter(budget??TimeSpan.FromMinutes(3));var watch=Stopwatch.StartNew();
            try{await operation(limit.Token).ConfigureAwait(false);_results.Add(new(name,"Passed",watch.ElapsedMilliseconds));}
            catch(CapabilityLimitedException error){_results.Add(new(name,"CapabilityLimited",watch.ElapsedMilliseconds,error.Message));}
            catch(NotSupportedException error){_results.Add(new(name,"CapabilityLimited",watch.ElapsedMilliseconds,error.Message));}
            catch(OperationCanceledException)when(stopping.IsCancellationRequested){throw;}
            catch(Exception error){_results.Add(new(name,"Failed",watch.ElapsedMilliseconds,Failure(error)));}
            finally{Save();Console.WriteLine($"{DateTimeOffset.UtcNow:O} {_phase} {name}: {_results.LastOrDefault()?.State}; elapsed={_elapsed.Elapsed.TotalMinutes:F1}m round={_round}");}
        }
        public async Task<string> RunAsync(CancellationToken token)
        {
            try
            {
                // 创建回执在响应头阶段记账，后续元数据失败也不能遗失已创建集合的清理责任。
                var handler=new OwnershipHandler(new SocketsHttpHandler{AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(15)},request=>request.Method.Method=="MKCOL"&&request.RequestUri?.AbsoluteUri==RootUri,()=>_created=true);
                _base=Source(_endpoint,"base-cache",handler);await _base.ProbeAsync(token).ConfigureAwait(false);
                if(await _base.TryGetMetadataAsync(_remoteName,token).ConfigureAwait(false) is not null)throw new IOException("本次唯一远端名称已经存在，拒绝接管。");
                _source=Source(RootUri,"protocol-cache");_raw=new(new SocketsHttpHandler{AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(15)}){Timeout=TimeSpan.FromSeconds(40)};
                _raw.DefaultRequestHeaders.Authorization=new("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(_credentials.UserName+":"+_credentials.Password)));
                _phase="CreatingRemoteRoot";Save();await _base.CreateDirectoryAsync(_remoteName,token).ConfigureAwait(false);_created=true;
                _started=DateTimeOffset.UtcNow;_elapsed.Start();_phase="ProtocolMatrix";Save();
                await ProtocolMatrix(token).ConfigureAwait(false);await SetupMount(token).ConfigureAwait(false);
                _phase="CfApiMatrix";
                await Case("CFAPI cold hydration SHA256",async ct=>{var bytes=await File.ReadAllBytesAsync(Path.Combine(Local,"seed.bin"),ct);Require(Digest(bytes)==Digest(Payload(1024*1024+37,17)),"冷水合摘要不同。");},token);
                await Case("pin release and rehydrate",ct=>CacheRound("seed.bin",ct),token);
                await Case("new directory unicode zero-byte upload",async ct=>{Track("mount/中文目录/零字节 #%.txt");Directory.CreateDirectory(Path.Combine(Local,"中文目录"));await File.WriteAllBytesAsync(Path.Combine(Local,"中文目录","零字节 #%.txt"),[],ct);await Uploaded("中文目录/零字节 #%.txt",[],ct);},token);
                await Case("editor atomic replacement",async ct=>{var path="editor.txt";Track("mount/"+path);Track("mount/editor.tmp");await File.WriteAllTextAsync(Path.Combine(Local,path),"before",ct);await Uploaded(path,"before"u8.ToArray(),ct);if(!_updates)throw new CapabilityLimitedException("服务未验证安全更新，原子替换仅验证冲突保护。");await File.WriteAllTextAsync(Path.Combine(Local,"editor.tmp"),"after replace",ct);File.Move(Path.Combine(Local,"editor.tmp"),Path.Combine(Local,path),true);await Uploaded(path,"after replace"u8.ToArray(),ct);},token);
                await Case("local delete keeps remote",async ct=>{Track("mount/local-delete.txt");await File.WriteAllTextAsync(Path.Combine(Local,"local-delete.txt"),"keep remote",ct);await Uploaded("local-delete.txt","keep remote"u8.ToArray(),ct);File.Delete(Path.Combine(Local,"local-delete.txt"));await Task.Delay(2500,ct);await using var remote=Source(RootUri+"mount/","verify-cache");Require(await remote.TryGetMetadataAsync("local-delete.txt",ct) is not null,"本地删除传播到远端。");},token);
                await Case("commit guard release edit F01",GuardReleaseEdit,token);
                await Case("queued directory replaced by file F02",TypeConflict,token);
                await Case("remote conflict and copy",ConflictCopy,token);
                await Case("remote delete cleans pinned file",RemoteDelete,token);
                await Case("stop reconnect and persistent queue",Reconnect,token);
                if(_extended)await ExtendedMatrix(token).ConfigureAwait(false);
                if(_focused)
                {
                    _phase="FocusedMatrix";
                    await Case("upstream DAV create modify delete reflected by CFAPI",RemoteInbound,token);
                    await Case("independent DAV media cold thumbnails",Thumbnails,token,TimeSpan.FromMinutes(8));
                    await Case("malformed thumbnail safe fallback and switch",MalformedPreview,token);
                }
                _phase="ContinuousSoak";Save();
                while(_elapsed.Elapsed<TimeSpan.FromMinutes(_minutes))
                {
                    token.ThrowIfCancellationRequested();_round++;
                    await Case($"round {_round} filesystem upload and digest",ct=>SoakRound(ct),token);
                    if(_round%4==0)await Case($"round {_round} reconnect",Reconnect,token);
                    if(_round%3==0)await Case($"round {_round} pin and release",ct=>CacheRound("seed.bin",ct),token);
                    if(_round%7==0)await Case($"round {_round} explicit refresh",async ct=>{await Job("refresh",null,ct);},token);
                    if(_round%9==0)await Case($"round {_round} cancellation",CancelRead,token);
                    if(_extended&&_round%5==0)await Case($"round {_round} thumbnails switch and cache",PreviewToggle,token);
                    if(_results.Count(x=>x.State=="Failed")>8)throw new IOException("失败达到停止阈值，保留证据并清理；未宣称完成请求时长。");
                    if(_focused)break;
                    var remaining=TimeSpan.FromMinutes(_minutes)-_elapsed.Elapsed;if(remaining>TimeSpan.Zero)await Task.Delay(remaining<TimeSpan.FromSeconds(20)?remaining:TimeSpan.FromSeconds(20),token);
                }
                _phase=_results.Any(x=>x.State=="Failed")?"CompletedWithFailures":_focused?"FocusedCompleted":"Completed";
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){_phase="Canceled";}
            catch(Exception error){Console.WriteLine("SESSION_FAILURE "+Failure(error));_results.Add(new("session", "Failed",0,Failure(error)));_phase="StoppedBeforeRequestedDuration";}
            finally
            {
                _ended=DateTimeOffset.UtcNow;_elapsed.Stop();var finalPhase=_phase;_phase="Cleanup";Save();
                try{await Cleanup().ConfigureAwait(false);}catch(Exception error){_results.Add(new("cleanup","Failed",0,Failure(error)));}
                _phase=finalPhase;Save();if(_source is not null)await _source.DisposeAsync();if(_base is not null)await _base.DisposeAsync();_raw?.Dispose();
            }
            return Path.Combine(_work,"live-report.json");
        }
        private async Task ProtocolMatrix(CancellationToken token)
        {
            await Case("read-only probe and depth-one list",async ct=>{await _source!.ProbeAsync(ct);Require((await All(_source.ListAsync("",ct))).Count==0,"新建测试集合不为空。");},token);
            await Case("directory and unicode hierarchy",async ct=>{Track("protocol/中文目录");await _source!.CreateDirectoryAsync("protocol",ct);await _source.CreateDirectoryAsync("protocol/中文目录",ct);},token);
            foreach(var (name,size) in new[]{("empty.bin",0),("中文 # % 🐴.bin",65537),("large.bin",8*1024*1024+17)})
            {
                await Case("protocol upload read "+name,async ct=>{var path="protocol/中文目录/"+name;Track(path);var data=Payload(size,31+size);await using var content=new MemoryStream(data);var metadata=await _source!.UploadAsync(path,content,null,ct);Require(metadata.Size==data.Length,"上传长度不同。");Require(await RemoteDigest(_source,metadata,ct)==Digest(data),"协议读回摘要不同。");if(size>0){await Task.WhenAll(Enumerable.Range(0,4).Select(async i=>{var offset=Math.Min(size-1,i*(size/4));var buffer=new byte[Math.Min(4093,size-offset)];var count=await _source.ReadRangeAsync(metadata,offset,buffer,ct);Require(count==buffer.Length&&buffer.SequenceEqual(data[offset..(offset+count)]),"并发范围内容不同。");}));}},token);
            }
            await Case("conditional create update stale baseline",async ct=>
            {
                const string path="protocol/update.bin";Track(path);var a=Payload(1024,1);var b=Payload(1024,2);await using var first=new MemoryStream(a);var original=await _source!.UploadAsync(path,first,null,ct);
                try{await _source.UploadAsync(path,new MemoryStream(b),null,ct);throw new IOException("仅创建请求覆盖了已存在文件。");}catch(SourceConflictException){}
                try{var current=await _source.UploadWithPreconditionAsync(path,new MemoryStream(b),new(original.Version,Digest(a)),null,ct);Require(await RemoteDigest(_source,current,ct)==Digest(b),"条件更新摘要不同。");_updates=true;
                    try{await _source.UploadWithPreconditionAsync(path,new MemoryStream(a),new(original.Version,Digest(a)),null,ct);throw new IOException("旧基线覆盖了新版本。");}catch(SourceConflictException){}}
                catch(NotSupportedException){_updates=false;throw;}
            },token);
            await Case("file move and exact delete",async ct=>{const string a="protocol/move-source.bin",b="protocol/move-target.bin";Track(a);Track(b);var data=Payload(257,8);var original=await _source!.UploadAsync(a,new MemoryStream(data),null,ct);var moved=await _source.MoveAsync(a,b,original.Version,ct);Require(await _source.TryGetMetadataAsync(a,ct) is null&&await RemoteDigest(_source,moved,ct)==Digest(data),"移动源或目标不同。");await _source.DeleteAsync(b,moved.Version,ct);Require(await _source.TryGetMetadataAsync(b,ct) is null,"删除后目标仍存在。");},token);
            await Case("canceled streaming upload preserves explicit outcome",async ct=>{const string path="protocol/canceled.bin";Track(path);using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);cancel.CancelAfter(180);await using var slow=new SlowStream(Payload(2*1024*1024,44));try{await _source!.UploadAsync(path,slow,null,cancel.Token);throw new IOException("取消上传未停止。");}catch(Exception error)when(error is OperationCanceledException or SourceOutcomeUnknownException){}},token);
            await Case("temporary recovery has no remote residue",async ct=>
            {
                await _source!.RecoverTemporaryItemsAsync(ct);await _source.DrainTemporaryCleanupAsync(ct);
                foreach(var pending in _source.PendingTemporaryItems)
                {
                    Require(await _source.TryGetMetadataAsync(pending.Path,ct) is null,"协议临时对象仍在服务器，保持证据。");
                    _results.Add(new("absent foreign temporary record","ObservedIssue",0,"远端已不存在，但临时记录保留 "+pending.Stage+" 状态；不将其记为全部记录已清除。"));
                }
            },token);
        }
        private async Task SetupMount(CancellationToken token)
        {
            _phase="MountSetup";Track("mount");await _source!.CreateDirectoryAsync("mount",token);Track("mount/seed.bin");await _source.UploadAsync("mount/seed.bin",new MemoryStream(Payload(1024*1024+37,17)),null,token);
            Directory.CreateDirectory(Local);_repository=new(Path.Combine(_work,"data"),_registry);_logger=new(Path.Combine(_work,"logs.db"));_manager=new(_repository,_logger);
            try{_mount=await _manager.AddAsync("WebDAV 长时功能测试",Local,true,token,Options(RootUri+"mount/"),_credentials,true);}
            catch{_mount=_manager.Mounts.SingleOrDefault(item=>item.Definition.LocalPath.Equals(Local,StringComparison.OrdinalIgnoreCase))?.Definition;throw;}
        }
        private async Task<LocalUpload> Uploaded(string path,byte[] expected,CancellationToken token)
        {
            var until=DateTimeOffset.UtcNow.AddSeconds(70);
            while(DateTimeOffset.UtcNow<until)
            {
                token.ThrowIfCancellationRequested();var item=_repository!.Uploads(_mount!.Id).FirstOrDefault(x=>!x.IsCopy&&x.LocalPath==path);
                if(item?.State is "Failed" or "Conflict")throw new IOException($"上传 {path}: {item.State} {item.Error}");
                if(item?.State=="Completed")
                {
                    await using var source=Source(RootUri+"mount/","verify-cache");
                    try
                    {
                        var metadata=await source.GetMetadataAsync(path,token);
                        if(metadata.Size==expected.Length&&await IndependentDigest("mount/"+path,metadata,token)==Digest(expected))
                        {
                            var verified=_repository.Upload(item.Id);
                            if(verified?.State=="Completed"&&verified.Generation==item.Generation&&verified.ExpectedVersion==item.ExpectedVersion)return verified;
                        }
                    }
                    catch(SourceConflictException){ /* 连续更新期间旧观察被版本保护拒绝；在同一期限内重新取得已完成代次。 */ }
                }
                await Task.Delay(150,token);
            }
            throw new IOException("自动上传未在期限内完成："+path);
        }
        private async Task<MountOperationSnapshot> Job(string operation,string[]? paths,CancellationToken token)
        {
            var id=_manager!.Queue(_mount!.Id,operation,paths);using var cancel=token.Register(()=>_manager.Cancel(id));
            while(true){token.ThrowIfCancellationRequested();var state=_manager.GetOperation(id)!;if(state.State is not("Queued" or "Running")){var expected=operation=="refresh"&&state.State=="PartialFailure"&&state.Results.All(item=>item.Status=="Completed"||_expectedRefreshConflicts.Contains(item.Path));if(state.State!="Completed"&&!expected)throw new IOException($"任务 {operation}: {state.State} {state.Error}");return state;}await Task.Delay(100,token);}
        }
        private async Task CacheRound(string name,CancellationToken token)
        {
            var path=Path.Combine(Local,name);await Job("keepOnDevice",[path],token);Require(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length),"固定后内容不完整。");
            await Job("freeSpace",[path],token);Require(!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length),"释放后仍保留完整正文。");
            Require(Digest(await File.ReadAllBytesAsync(path,token))==Digest(Payload(1024*1024+37,17)),"再次水合摘要不同。");
        }
        private async Task GuardReleaseEdit(CancellationToken token)
        {
            if(!_updates)throw new CapabilityLimitedException("服务未验证安全更新，F01 实际连续更新受限。");
            const string name="guard-release.txt";Track("mount/"+name);var first="real commit A"u8.ToArray();var second="real commit B"u8.ToArray();var path=Path.Combine(Local,name);var injected=false;var captured=new TaskCompletionSource<LocalUpload>(TaskCreationOptions.RunContinuationsAsynchronously);string? editedStamp=null;
            _manager!.UploadCheckpointForTests=async(stage,item)=>{if(item.LocalPath!=name)return;if(stage=="CommitVerified"&&!injected){injected=true;await File.WriteAllBytesAsync(path,second,token);editedStamp=LocalFileState.Read(path).Key;}else if(stage=="Committed"&&injected&&!captured.Task.IsCompleted)captured.TrySetResult(item);};
            try{await File.WriteAllBytesAsync(path,first,token);var committed=await captured.Task.WaitAsync(TimeSpan.FromSeconds(60),token);Require(committed.ObservedStamp!=editedStamp,"F01：新编辑被吸收到已上传观察戳。");var final=await Uploaded(name,second,token);Require(final.Generation>committed.Generation,"新编辑没有下一代意图。");}
            finally{_manager.UploadCheckpointForTests=null;}
        }
        private async Task TypeConflict(CancellationToken token)
        {
            const string name="type-change";Track("mount/"+name);await _manager!.SetSchedulingForTestsAsync(_mount!.Id,false,token);
            try{var path=Path.Combine(Local,name);Directory.CreateDirectory(path);await _manager.ScanUploadChangesAsync(_mount.Id,token);Directory.Delete(path);await File.WriteAllTextAsync(path,"preserve new file",token);await _manager.ScanUploadChangesAsync(_mount.Id,token);var record=_repository!.Uploads(_mount.Id).Single(x=>x.LocalPath==name);Require(record.State=="Conflict"&&record.IsDirectory,"类型变化没有保持目录意图和冲突。");_expectedRefreshConflicts.Add(name);await using var source=Source(RootUri+"mount/","verify-cache");Require(await source.TryGetMetadataAsync(name,token) is null,"类型冲突已经发送到远端。");}
            finally{await _manager.SetSchedulingForTestsAsync(_mount!.Id,true,CancellationToken.None);}
        }
        private async Task ConflictCopy(CancellationToken token)
        {
            if(!_updates)throw new CapabilityLimitedException("服务未验证安全更新，远端并发更新受限。");
            const string name="conflict.txt",copy="conflict-copy.txt";Track("mount/"+name);Track("mount/"+copy);var a="baseline"u8.ToArray();await File.WriteAllBytesAsync(Path.Combine(Local,name),a,token);await Uploaded(name,a,token);
            await using var source=Source(RootUri+"mount/","verify-cache");var original=await source.GetMetadataAsync(name,token);await source.UploadWithPreconditionAsync(name,new MemoryStream("remote other"u8.ToArray()),new(original.Version,Digest(a)),null,token);
            await File.WriteAllTextAsync(Path.Combine(Local,name),"local preserved",token);LocalUpload? item=null;
            for(var i=0;i<400;i++){token.ThrowIfCancellationRequested();item=_repository!.Uploads(_mount!.Id).FirstOrDefault(x=>x.LocalPath==name);if(item?.State=="Conflict")break;await Task.Delay(100,token);}Require(item?.State=="Conflict","远端并发变化没有冲突。");_expectedRefreshConflicts.Add(name);
            var id=_manager!.UploadAsCopy(item!.Id,copy);for(var i=0;i<400;i++){token.ThrowIfCancellationRequested();var status=_manager.GetOperation(id);if(status?.State=="Completed")return;if(status?.State is "Failed" or "Conflict")throw new IOException("副本未完成："+status.Error);await Task.Delay(100,token);}throw new IOException("副本上传超时。");
        }
        private async Task RemoteDelete(CancellationToken token)
        {
            const string path="remote-delete.txt";Track("mount/"+path);await File.WriteAllTextAsync(Path.Combine(Local,path),"delete remote",token);await Uploaded(path,"delete remote"u8.ToArray(),token);await Job("keepOnDevice",[Path.Combine(Local,path)],token);
            await RawDelete("mount/"+path,token);await Job("refresh",null,token);Require(!File.Exists(Path.Combine(Local,path)),"远端删除后固定文件未清理。");
        }
        private async Task Reconnect(CancellationToken token){await _manager!.DisconnectAsync(_mount!.Id,token);await _manager.ConnectAsync(_mount.Id,token);Require(_manager.Mounts.Single(x=>x.Definition.Id==_mount.Id).Status=="已连接","重连没有恢复。");}
        private async Task SoakRound(CancellationToken token)
        {
            var path=$"soak-{_round:D4}.bin";Track("mount/"+path);var sizes=new[]{0,31,4097,65537,1024*1024+3,4*1024*1024+7};var data=Payload(sizes[_round%sizes.Length],_round);
            await File.WriteAllBytesAsync(Path.Combine(Local,path),data,token);await Uploaded(path,data,token);
            if(_updates&&_round%2==0){var changed=Payload(data.Length+17,_round+1024);await File.WriteAllBytesAsync(Path.Combine(Local,path),changed,token);await Uploaded(path,changed,token);}
            if(_round%5==0){File.Delete(Path.Combine(Local,path));await Task.Delay(2200,token);await using var source=Source(RootUri+"mount/","verify-cache");Require(await source.TryGetMetadataAsync(path,token) is not null,"补扫把本地删除传播到远端。");}
        }
        private async Task CancelRead(CancellationToken token)
        {
            await using var source=Source(RootUri,"cancel-cache");var metadata=await source.GetMetadataAsync("protocol/中文目录/large.bin",token);using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{await source.ReadRangeAsync(metadata,0,new byte[65536],cancel.Token);throw new IOException("已取消读取仍然执行。");}catch(OperationCanceledException){}
            var bytes=new byte[17];Require(await source.ReadRangeAsync(metadata,7,bytes,token)==17,"取消后正常读取未恢复。");
        }
        private async Task Cleanup()
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=cleanup.Token;
            var errors=new List<Exception>();
            if(_manager is not null)
            {
                try{if(_mount is not null)await Job("clearAndRemove",null,token);}catch(Exception error){errors.Add(error);}
                try{await _manager.DisposeAsync();}catch(Exception error){errors.Add(error);}
            }
            if(_logger is not null)try{await _logger.DisposeAsync();}catch(Exception error){errors.Add(error);}
            // 本地清理未成功时保留注册表和索引供恢复，不能把残留注册伪装成已清理。
            if(errors.Count==0)Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_registry,false);
            if(errors.Count!=0)_results.Add(new("local cleanup","Failed",0,"本地清理未全部完成，保留恢复资料。"));
            if(!_created||_source is null)return;
            await _source.RecoverTemporaryItemsAsync(token);await _source.DrainTemporaryCleanupAsync(token);var inventory=new List<SourceEntry>();
            async Task Collect(string parent){await foreach(var item in _source.ListAsync(parent,token)){inventory.Add(item);if(item.IsDirectory)await Collect(item.Path);}}
            await Collect("");var unknown=inventory.Where(x=>!_known.Contains(x.Path)).ToArray();if(unknown.Length!=0)throw new IOException("存在不在本轮清单中的远端项，拒绝递归删除："+string.Join(",",unknown.Select(x=>x.Path).Take(8)));
            foreach(var item in inventory.OrderByDescending(x=>x.Path.Count(c=>c=='/')).ThenBy(x=>x.IsDirectory?1:0))await RawDelete(item.Path,token,item.IsDirectory);
            using var request=new HttpRequestMessage(HttpMethod.Delete,RootUri);using var response=await _raw!.SendAsync(request,token);Require(response.IsSuccessStatusCode||response.StatusCode==HttpStatusCode.NotFound,"测试根清理失败。");
            Require(await _base!.TryGetMetadataAsync(_remoteName,token) is null,"测试根删除后仍存在。");_results.Add(new("precise cleanup","Passed",0));
        }
        private async Task RawDelete(string path,CancellationToken token,bool directory=false)
        {
            if(!_known.Contains(path))throw new IOException("拒绝清理未记账路径。");var uri=RootUri+string.Join('/',RelativePaths.Normalize(path).Split('/').Select(Uri.EscapeDataString))+(directory?"/":"");using var request=new HttpRequestMessage(HttpMethod.Delete,uri);using var response=await _raw!.SendAsync(request,token);
            Require(response.IsSuccessStatusCode||response.StatusCode==HttpStatusCode.NotFound,"本轮数据清理 HTTP "+(int)response.StatusCode);
        }
        private static async Task<List<SourceEntry>> All(IAsyncEnumerable<SourceEntry> items){var result=new List<SourceEntry>();await foreach(var item in items)result.Add(item);return result;}
        private static async Task<string> RemoteDigest(IFileSource source,SourceEntry metadata,CancellationToken token)
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[256*1024];long offset=0;
            while(offset<metadata.Size){var count=await source.ReadRangeAsync(metadata,offset,buffer.AsMemory(0,(int)Math.Min(buffer.Length,metadata.Size-offset)),token);Require(count>0,"正文提前结束。");hash.AppendData(buffer,0,count);offset+=count;}return Convert.ToHexString(hash.GetHashAndReset());
        }
        private static string Digest(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
        private static byte[] Payload(int length,int seed){var result=new byte[length];new Random(seed).NextBytes(result);return result;}
        private static void Require(bool condition,string message){if(!condition)throw new IOException(message);}
    }
    /// <summary>只保存创建回执的归属，不记录授权头、命令行、响应正文或锁令牌。</summary>
    private sealed class OwnershipHandler(HttpMessageHandler inner,Func<HttpRequestMessage,bool> owns,Action created):DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){var response=await base.SendAsync(request,token);if(response.StatusCode==HttpStatusCode.Created&&owns(request))created();return response;}
    }
    /// <summary>慢速不可变输入用于真实网络取消，不修改服务器或系统网络配置。</summary>
    private sealed class SlowStream(byte[] bytes):MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){await Task.Delay(40,token);return await base.ReadAsync(buffer[..Math.Min(buffer.Length,32768)],token);}
        public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token){await Task.Delay(40,token);return await base.ReadAsync(buffer,offset,Math.Min(count,32768),token);}
    }
}
