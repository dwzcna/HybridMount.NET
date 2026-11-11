using System.Runtime.CompilerServices;
using System.Text;
namespace HybridMount.Tests;

/// <summary>隔离NTFS根验证先分批出占位、取消重做、稳定身份和元数据／正文边界，不访问正式挂载。</summary>
[Collection("Native IPC")]
public sealed class LargeDirectoryDiscoveryTests
{
    [Fact(Timeout=120000)]
    public async Task FirstBatchIsVisibleBeforeEnumerationCompletesAndNoBodyIsRead()
    {
        var gate=new ListingGate(300);await using var area=new Area(1000,gate);var connect=area.Connect();
        try
        {
            await gate.Paused.Task.WaitAsync(TimeSpan.FromSeconds(30));Assert.False(connect.IsCompleted);Assert.Equal(256,area.Repository.Entries(area.Id).Count);area.AssertMaterialized();
            Assert.DoesNotContain(area.Dav.Server.Requests,request=>request.Method=="GET");gate.Release.TrySetResult();await connect;
            area.AssertMaterialized(complete:true);var entries=area.Repository.Entries(area.Id);Assert.All(entries,entry=>Assert.True(entry.Materialized&&!entry.Protected&&!entry.Deleted));
            Assert.DoesNotContain(area.Dav.Server.Requests,request=>request.Method=="GET");Assert.DoesNotContain(area.Repository.AllRemotePending(area.Id),item=>item.State!="Completed"&&item.State!="Superseded");
        }
        finally{gate.Release.TrySetResult();try{await connect;}catch(Exception){}}
    }

    [Fact(Timeout=120000)]
    public async Task CancellationKeepsCommittedBatchAndReconnectDeduplicatesItsStableIds()
    {
        var gate=new ListingGate(300);await using var area=new Area(1000,gate);using var cancel=new CancellationTokenSource();var connect=area.Connect(cancel.Token);
        try
        {
            await gate.Paused.Task.WaitAsync(TimeSpan.FromSeconds(30));var id=area.Id;var before=area.Repository.Entries(id).ToDictionary(item=>item.SourcePath,item=>item.Id);Assert.Equal(256,before.Count);
            cancel.Cancel();var failure=await Record.ExceptionAsync(()=>connect);Assert.IsAssignableFrom<OperationCanceledException>(failure);Assert.Equal(256,area.Repository.Entries(id).Count);area.AssertMaterialized();
            Assert.True(area.Repository.RemoteCollections(id,area.Repository.AllRemotePending(id).First().Scope).Single(item=>item.Path=="").BuildingBaseline);
            gate.HoldAfter=0;gate.Release.TrySetResult();await area.Manager.ConnectAsync(id);var after=area.Repository.Entries(id).ToDictionary(item=>item.SourcePath,item=>item.Id);
            area.AssertMaterialized(complete:true);foreach(var pair in before)Assert.Equal(pair.Value,after[pair.Key]);Assert.DoesNotContain(area.Dav.Server.Requests,request=>request.Method=="GET");
        }
        finally{gate.Release.TrySetResult();try{await connect;}catch(Exception){}}
    }

    [Fact(Timeout=30000)]
    public async Task FailedPartialEnumerationCannotPublishDeletionAndNextFullScanCanConfirmIt()
    {
        using var directory=new Scratch();var definitions=new Definitions();var repository=new MountRepository(directory.Path,definitions:definitions);var mount=Guid.NewGuid();const string scope="large64";
        var old=new SourceEntry("old","old.txt",false,4,DateTimeOffset.FromUnixTimeSeconds(1700000000),"old-v1");repository.SaveEntry(mount,new("old-id",old.Path,old.Path,old.Version,false,Materialized:true));repository.CommitRemoteBatch(mount,scope,new("",Mode:"Full"),[new(old)],[]);
        await using var dav=new DavFixture();for(var i=0;i<600;i++)dav.Put("bulk-"+i.ToString("D5")+".bin",[1,2,3]);var gate=new ListingGate(300);await using var source=new SlowListing(dav.Source(),gate);
        var engine=new RemoteSyncEngine(mount,scope,source,source,repository);using var cancel=new CancellationTokenSource();var poll=engine.PollAsync(true,TimeSpan.Zero,cancel.Token);
        await gate.Paused.Task;Assert.Contains(repository.RemoteShadow(mount,scope),item=>item.Entry.Path=="old.txt");Assert.DoesNotContain(repository.RemotePending(mount,scope),item=>item.Entry is null);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>poll);Assert.Contains(repository.RemoteShadow(mount,scope),item=>item.Entry.Path=="old.txt");Assert.DoesNotContain(repository.RemotePending(mount,scope),item=>item.Entry is null);
        gate.HoldAfter=0;gate.Release.TrySetResult();await engine.PollAsync(true,TimeSpan.Zero,default);Assert.Contains(repository.RemotePending(mount,scope),item=>item.Path=="old.txt"&&item.Entry is null);
    }

    [Fact]
    public void ConcurrentColdBindingOnlySharesTheSameVerifiedVersionAndKeepsProtectionBarriers()
    {
        using var directory=new Scratch();var repository=new MountRepository(directory.Path,definitions:new Definitions());var mount=Guid.NewGuid();var raw=new MountEntry("id","file.bin","file.bin","s:8;m:10;c:1",false,Materialized:true);repository.SaveEntry(mount,raw);
        var first=raw.Version+";md5:"+new string('A',32);var other=raw.Version+";md5:"+new string('B',32);
        Assert.False(repository.BindColdObservedVersion(mount,raw,first,false));Assert.True(repository.BindColdObservedVersion(mount,raw,first,true));Assert.True(repository.BindColdObservedVersion(mount,raw,first,false));Assert.False(repository.BindColdObservedVersion(mount,raw,other,false));
        repository.SaveEntry(mount,repository.EntryById(mount,raw.Id)! with{Protected=true});Assert.False(repository.BindColdObservedVersion(mount,raw,first,false));Assert.True(repository.EntryById(mount,raw.Id)!.Protected);
    }

    [Fact(Timeout=60000)]
    public async Task NestedDirectoriesPublishParentsFirstAndRecoverAnUncommittedCreationReceipt()
    {
        var gate=new ListingGate(0);await using var area=new Area(0,gate);
        area.Dav.Objects["one"]=new([],"\"dir1\"",true);area.Dav.Objects["one/two"]=new([],"\"dir2\"",true);area.Dav.Put("one/two/leaf.bin",[1,2,3]);
        await area.Connect();area.AssertMaterialized(complete:true);await area.Manager.SetRemoteDetectionAsync(area.Id,false,60,300);await area.Manager.DisconnectAsync(area.Id);
        var entry=area.Repository.Entries(area.Id).Single(item=>item.SourcePath=="one/two/leaf.bin");var stamp=LocalFileState.Read(RelativePaths.ToLocal(area.Local,entry.LocalPath));
        // 重建“CFAPI已成功、发布事务未提交”的持久边界；常规SaveEntry故意不允许回退物化标志。
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(area.Repository.DataDirectory,"mounts.db"),Pooling=false}.ToString())){db.Open();using var update=db.CreateCommand();update.CommandText="UPDATE entries SET materialized=0 WHERE mount=$m AND id=$id";update.Parameters.AddWithValue("$m",area.Id.ToString("N"));update.Parameters.AddWithValue("$id",entry.Id);Assert.Equal(1,update.ExecuteNonQuery());}
        Assert.False(area.Repository.EntryById(area.Id,entry.Id)!.Materialized);var pending=area.Repository.AllRemotePending(area.Id).Last(item=>item.Path==entry.SourcePath);area.Repository.SaveRemotePending(pending with{State="Pending"});
        await area.Manager.ConnectAsync(area.Id);area.AssertMaterialized(complete:true);Assert.Equal(entry.Id,area.Repository.Entries(area.Id).Single(item=>item.SourcePath==entry.SourcePath).Id);Assert.Equal(stamp.Identity,LocalFileState.Read(RelativePaths.ToLocal(area.Local,entry.LocalPath)).Identity);Assert.Equal("Completed",area.Repository.FindRemotePending(pending.Id)!.State);
        Assert.DoesNotContain(area.Dav.Server.Requests,request=>request.Method=="GET");
    }
    [Fact(Timeout=120000)]
    public async Task LocalCollisionAfterFirstBatchNeverOverwritesOrdinaryFileAndOtherItemsContinue()
    {
        var gate=new ListingGate(300);await using var area=new Area(600,gate);var connect=area.Connect();
        try
        {
            await gate.Paused.Task.WaitAsync(TimeSpan.FromSeconds(30));var known=area.Repository.Entries(area.Id).Select(item=>item.SourcePath).ToHashSet();var collision=area.Dav.Objects.First(item=>!item.Value.Directory&&!known.Contains(item.Key)).Key;var path=RelativePaths.ToLocal(area.Local,collision);await File.WriteAllTextAsync(path,"local content must survive");
            gate.Release.TrySetResult();await connect;Assert.Equal("local content must survive",await File.ReadAllTextAsync(path));Assert.Contains(area.Repository.AllRemotePending(area.Id),item=>item.Path==collision&&item.State=="Conflict");Assert.True(area.Repository.Entries(area.Id).Count(item=>item.Materialized)>=600);
        }
        finally{gate.Release.TrySetResult();try{await connect;}catch(Exception){}}
    }
    [Fact(Timeout=60000)]
    public async Task S3LatePageFailureKeepsPublishedBatchWithoutPublishingDeletion()
    {
        await using var fixture=new S3Fixture();for(var i=0;i<270;i++)fixture.Put("remote/bulk-"+i.ToString("D5")+".bin",[1]);await using var source=fixture.Source();var repository=new MountRepository(Path.Combine(fixture.Area,"stream-data"),definitions:new Definitions());var id=Guid.NewGuid();const string scope="s3-large64";
        var old=new SourceEntry("old","gone.bin",false,1,DateTimeOffset.FromUnixTimeSeconds(1700000000),"old-v1");repository.CommitRemoteBatch(id,scope,new("",Mode:"Full"),[new(old)],[]);var published=0;var engine=new RemoteSyncEngine(id,scope,source,source,repository){PublishDiscovered=(batch,_)=>{Assert.InRange(batch.Count,1,256);published+=batch.Count;return Task.FromResult<IReadOnlyList<MountEntry>>([]);}};
        var original=fixture.Server.Handler;fixture.Server.Handler=request=>request.Target.Contains("continuation-token=page-260")?Task.FromResult(new HttpTestServer.Response(503,[])):original(request);
        await Assert.ThrowsAnyAsync<IOException>(()=>engine.PollAsync(true,TimeSpan.Zero,default));Assert.Equal(256,published);Assert.Contains(repository.RemoteShadow(id,scope),item=>item.Entry.Path=="gone.bin");Assert.DoesNotContain(repository.RemotePending(id,scope),item=>item.Entry is null);Assert.True(repository.RemoteCollections(id,scope).Single().BuildingBaseline);
        fixture.Server.Handler=original;await engine.PollAsync(true,TimeSpan.Zero,default);Assert.Contains(repository.RemotePending(id,scope),item=>item.Path=="gone.bin"&&item.Entry is null);Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="GET"&&!request.Target.Contains("list-type="));
    }
    private sealed class ListingGate(int after)
    {public int HoldAfter=after;public readonly TaskCompletionSource Paused=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    /// <summary>只延迟返回已解析元数据，保持真实WebDAV传输与读取验证；不伪造内核占位状态。</summary>
    private sealed class SlowListing(WebDavFileSource source,ListingGate gate):IFileSource,IRemoteChangeSource
    {
        public FileSourceCapabilities Capabilities=>source.Capabilities;public SourceWriteCapabilities WriteCapabilities=>source.WriteCapabilities;
        public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)=>source.GetMetadataAsync(path,token);
        public ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)=>source.ReadRangeAsync(entry,offset,buffer,token);
        public Task<SourceEntry> UploadAsync(string path,Stream bytes,string? expected,CancellationToken token=default)=>source.UploadAsync(path,bytes,expected,token);
        public Task DeleteAsync(string path,string expected,CancellationToken token=default)=>source.DeleteAsync(path,expected,token);
        public Task<SourceEntry> MoveAsync(string path,string destination,string expected,CancellationToken token=default)=>source.MoveAsync(path,destination,expected,token);
        public Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)=>source.CreateDirectoryAsync(path,token);
        public async IAsyncEnumerable<SourceEntry> ListAsync(string path,[EnumeratorCancellation]CancellationToken token=default)
        {var count=0;await foreach(var entry in source.ListAsync(path,token).ConfigureAwait(false)){if(gate.HoldAfter>0&&++count==gate.HoldAfter){gate.Paused.TrySetResult();await gate.Release.Task.WaitAsync(token).ConfigureAwait(false);}yield return entry;}}
        public Task<bool> SupportsCollectionSyncAsync(string collection,CancellationToken token)=>Task.FromResult(false);
        public Task<RemoteChangeBatch> GetCollectionChangesAsync(string collection,string? syncToken,CancellationToken token)=>throw new NotSupportedException();
        public SourceEntry WithObservation(SourceEntry entry,long observation)=>source.WithObservation(entry,observation);
        public void RestoreObservations(IEnumerable<RemoteShadowEntry> entries)=>source.RestoreObservations(entries);
        public bool IsSameVersionHint(string expected,string observed)=>source.IsSameVersionHint(expected,observed);
        public bool HasReliableVersion(SourceEntry entry)=>source.HasReliableVersion(entry);
        public bool IsSameVersion(string expected,string observed)=>source.IsSameVersion(expected,observed);
        public ValueTask DisposeAsync()=>source.DisposeAsync();
    }
    private sealed class Factory(DavFixture dav,ListingGate gate):IFileSourceFactory
    {
        public async Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)
        {var source=dav.Source();if(TasksQueueService.CurrentBinding is {} binding)((IMountNetworkSource)source).BindQueue(binding);await source.ProbeAsync(token);return new SlowListing(source,gate);}
    }
    private sealed class Definitions:IMountDefinitionStore
    {public readonly Dictionary<Guid,MountDefinition> Values=[];public IReadOnlyList<MountDefinition> LoadDefinitions()=>Values.Values.ToArray();public void SaveDefinition(MountDefinition value)=>Values[value.Id]=value;public void DeleteDefinition(Guid id)=>Values.Remove(id);public long CacheLimitBytes{get;set;}}
    private sealed class Area:IAsyncDisposable
    {
        public readonly DavFixture Dav=new();private readonly Definitions _definitions=new();private readonly LoggerService _logger;
        public readonly string Local;public readonly MountRepository Repository;public readonly MountManager Manager;
        public Guid Id=>_definitions.Values.Keys.Single();
        public Area(int count,ListingGate gate)
        {for(var i=0;i<count;i++)Dav.Put("bulk-"+i.ToString("D5")+".bin",Encoding.UTF8.GetBytes("data"));Local=System.IO.Path.Combine(Dav.Area,"view");Directory.CreateDirectory(Local);Repository=new(System.IO.Path.Combine(Dav.Area,"data"),definitions:_definitions);_logger=new(System.IO.Path.Combine(Dav.Area,"logs.db"));Manager=new(Repository,_logger,new Factory(Dav,gate));}
        /// <summary>按远端条目核验占位，避免将同步根的desktop.ini计为用户文件。</summary>
        public void AssertMaterialized(bool complete=false)
        {
            var entries=Repository.Entries(Id);if(complete)Assert.Equal(Dav.Objects.Keys.Count(path=>path.Length>0),entries.Count);
            foreach(var entry in entries){var path=RelativePaths.ToLocal(Local,entry.LocalPath);Assert.True(entry.IsDirectory?Directory.Exists(path):File.Exists(path),entry.LocalPath);var info=CloudFilesNative.Inspect(path);Assert.NotNull(info);var marker=MountPathGuard.ReadEntry(info.Value.Identity);Assert.Equal(Id,marker?.MountId);Assert.Equal(entry.Id,marker?.EntryId);}
        }
        public async Task Connect(CancellationToken token=default)=>_ = await Manager.AddAsync("large64",Local,false,token,source:Dav.Options,credentials:new("user","app-password"),autoUpload:false);
        public async ValueTask DisposeAsync()
        {try{if(_definitions.Values.Count!=0){var id=Id;var result=await WebDavTests.Finished(Manager,Manager.Queue(id,"clearAndRemove"));Assert.Equal("Completed",result.State);}}finally{await Manager.DisposeAsync();await _logger.DisposeAsync();await Dav.DisposeAsync();}}
    }
    private sealed class Scratch:IDisposable
    {public string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HybridMount.Large64",Guid.NewGuid().ToString("N"));public Scratch()=>Directory.CreateDirectory(Path);public void Dispose(){if(Directory.Exists(Path))Directory.Delete(Path,true);}}
}
