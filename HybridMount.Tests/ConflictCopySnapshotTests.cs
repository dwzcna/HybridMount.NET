using System.Reflection;
using System.Security.Cryptography;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.ViewModels;

namespace HybridMount.Tests;

public sealed class ConflictCopySnapshotTests
{
    [Theory]
    [InlineData(0)][InlineData(3)][InlineData(262145)]
    public async Task CopyFreezesSavedBytesDespiteLaterLocalEditsAndUploadsWithCreateGuard(int size)
    {
        await using var test=new Fixture(size);var original=test.Repository.Upload(test.Original.Id)!;File.WriteAllText(test.Local,"later edits with different length");test.Repository.UpdateUpload(original.Id,x=>x with{Generation=9,ObservedStamp=LocalFileState.Read(test.Local).Key});var before=test.Repository.Upload(original.Id)!;
        var copy=test.Copy();Assert.Equal(before,test.Repository.Upload(original.Id));Assert.Null(copy.EntryId);Assert.Null(copy.ExpectedVersion);Assert.Null(copy.BaselineSha256);Assert.True(copy.IsCopy);Assert.True(copy.ExplicitRequest);Assert.Equal(1,copy.Generation);Assert.Equal(1,copy.SnapshotGeneration);Assert.Equal(size,copy.Total);Assert.Equal(original.SnapshotPath,copy.SnapshotPath);Assert.Equal(original.SnapshotSha256,copy.SnapshotSha256);
        File.Delete(test.Local);var prepared=UploadCopySnapshot.Prepare(copy,_=>{});Assert.Equal("Prepared",prepared.State);
        await using var remote=new S3Fixture();await using var source=remote.Source();await using var content=File.OpenRead(prepared.SnapshotPath!);var receipt=await source.UploadWithPreconditionAsync(prepared.SourcePath,content,new(prepared.ExpectedVersion,prepared.BaselineSha256),null,default);Assert.Equal(size,receipt.Size);Assert.Equal(test.Bytes,remote.Objects["remote/copy"].Bytes);
        Assert.Equal(before,test.Repository.Upload(original.Id));Assert.True(test.Repository.Entries(test.Mount.Id).Single().Protected);
    }
    [Fact]
    public async Task SharedSnapshotSurvivesOriginalReplacementAndRestartUntilCopyCompletes()
    {
        await using var test=new Fixture(9);var copy=test.Copy();var next=Path.Combine(Path.GetDirectoryName(test.Snapshot)!,"next.snapshot");File.WriteAllText(next,"new generation");test.Repository.UpdateUpload(test.Original.Id,x=>x with{SnapshotPath=next,Generation=2});test.Delete(test.Snapshot);Assert.True(File.Exists(test.Snapshot));
        var restored=new MountRepository(test.Repository.DataDirectory,definitions:test.Definitions);var recovered=restored.Upload(copy.Id)!;var prepared=UploadCopySnapshot.Prepare(recovered,_=>{});Assert.Equal(test.Bytes,File.ReadAllBytes(prepared.SnapshotPath!));
        var original=restored.Upload(test.Original.Id);var entries=restored.Entries(test.Mount.Id);var receipt=new SourceEntry("remote-copy","copy",false,test.Bytes.Length,DateTimeOffset.UtcNow,"created");restored.CommitUploadedEntry(prepared,new("copy-entry","copy","file",receipt.Version,false),receipt,null);test.Delete(test.Snapshot);Assert.False(File.Exists(test.Snapshot));Assert.True(File.Exists(next));Assert.Equal(original,restored.Upload(test.Original.Id));Assert.Equal(entries,restored.Entries(test.Mount.Id));
    }
    [Fact]
    public async Task MultipleCopiesAndOriginalRetainSharedSnapshotIndependently()
    {
        await using var test=new Fixture(1);var first=test.Copy();var second=test.Copy("copy-two");test.Repository.UpdateUpload(first.Id,x=>x with{SnapshotPath=null,State="Completed"});test.Delete(test.Snapshot);Assert.True(File.Exists(test.Snapshot));test.Repository.UpdateUpload(test.Original.Id,x=>x with{SnapshotPath=null});test.Delete(test.Snapshot);Assert.True(File.Exists(test.Snapshot));test.Repository.UpdateUpload(second.Id,x=>x with{SnapshotPath=null,State="Completed"});test.Delete(test.Snapshot);Assert.False(File.Exists(test.Snapshot));
    }
    [Fact]
    public async Task ForgetCanDeleteSnapshotsWhileCanceledRecordsStillRetainThem()
    {
        await using var test=new Fixture(1);var copy=test.Copy();test.Repository.UpdateUpload(copy.Id,x=>x with{State="Canceled",CancelRequested=true});test.Delete(test.Snapshot);Assert.True(File.Exists(test.Snapshot));test.Delete(test.Snapshot,true);Assert.False(File.Exists(test.Snapshot));
    }
    [Theory]
    [InlineData("missing")][InlineData("hash")][InlineData("stamp")][InlineData("outside")][InlineData("directory")]
    public async Task UnusableSavedSnapshotNeverFallsBackToCurrentLocalBytes(string problem)
    {
        await using var test=new Fixture(2);if(problem=="missing")File.Delete(test.Snapshot);
        test.Repository.UpdateUpload(test.Original.Id,x=>problem switch{"hash"=>x with{SnapshotSha256=null},"stamp"=>x with{SnapshotStamp=null},"outside"=>x with{SnapshotPath=test.Local},"directory"=>x with{IsDirectory=true},_=>x});
        Assert.ThrowsAny<IOException>(()=>test.Manager.UploadAsCopy(test.Original.Id,"copy"));Assert.DoesNotContain(test.Repository.Uploads(),x=>x.IsCopy);
    }
    [Theory]
    [InlineData("missing")][InlineData("length")][InlineData("generation")][InlineData("stamp")]
    public async Task FrozenCopyPreparationRejectsLostOrInvalidSnapshots(string problem)
    {
        await using var test=new Fixture(2);var copy=test.Copy();if(problem=="missing")File.Delete(test.Snapshot);if(problem=="length")File.AppendAllText(test.Snapshot,"changed");copy=problem switch{"generation"=>copy with{Generation=2},"stamp"=>copy with{SnapshotStamp=null},_=>copy};Assert.Throws<SourceConflictException>(()=>UploadCopySnapshot.Prepare(copy,_=>{}));
    }
    [Fact]
    public async Task NoSnapshotPreservesLegacyQueueAndDialogExplicitlyDisclosesCurrentContent()
    {
        await using var test=new Fixture(1);test.Repository.UpdateUpload(test.Original.Id,x=>x with{SnapshotPath=null,SnapshotSha256=null,SnapshotStamp=null,SnapshotIdentity=null,SnapshotGeneration=0});var copy=test.Copy();Assert.Null(copy.SnapshotPath);Assert.Equal("Queued",copy.State);var vm=new TasksViewModel(test.Manager,new Dispatcher());
        try{Assert.Contains("没有快照时",vm.CopyContentNotice);Assert.Contains("上传准备时的当前本地内容",vm.CopyContentNotice);Assert.Contains("不是冲突时的内容",vm.CopyContentNotice);}
        // 页面关闭不等待UI；隔离夹具删除数据库前必须异步等待已开始的只读快照收尾。
        finally{vm.Dispose();await vm.SnapshotIdle;}
    }
    [Theory]
    [InlineData("")][InlineData("file")][InlineData("../outside")]
    public async Task InvalidCopyDestinationDoesNotCreateReferences(string destination)
    {
        await using var test=new Fixture(1);Assert.ThrowsAny<ArgumentException>(()=>test.Manager.UploadAsCopy(test.Original.Id,destination));Assert.DoesNotContain(test.Repository.Uploads(),x=>x.IsCopy);
    }
    [Fact(Timeout=20000)]
    public async Task CopyRegistrationAndSnapshotDeletionAreSerialized()
    {
        await using var test=new Fixture(1);using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var copy=Task.Run(()=>test.Repository.CreateUploadCopy(test.Original.Id,"copy",_=>{entered.Set();release.Wait();}));Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var deletion=Task.Run(()=>test.Repository.DeleteUnreferencedUploadSnapshot(test.Snapshot,()=>File.Delete(test.Snapshot)));try{Assert.False(deletion.IsCompleted);}finally{release.Set();}await copy;await deletion;Assert.True(File.Exists(test.Snapshot));
    }
    /// <summary>使用真实NTFS／CFAPI挂载及上传工作器，冻结A后编辑B或删除原文件，副本仍只发送A且不提交原文件。</summary>
    [Theory(Timeout=120000)]
    [InlineData(false)][InlineData(true)]
    public async Task ActualWorkerUploadsFrozenContentAfterOriginalEditOrDeletion(bool removeOriginal)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var bytes=new byte[]{1,2,3,4};var original=await ConflictedSnapshot(test,bytes);
        var path=Path.Combine(test.Local,"frozen-source.bin");var later=new byte[]{8,9,10,11,12};if(removeOriginal)File.Delete(path);else await File.WriteAllBytesAsync(path,later);
        await test.Manager.ScanUploadChangesAsync(test.Id);var before=test.Repository.Upload(original.Id)!;Assert.Equal(original.SnapshotPath,before.SnapshotPath);
        var id=test.Manager.UploadAsCopy(original.Id,"frozen-copy.bin");var completed=await WaitCopy(test,id,"Completed");
        Assert.True(completed.IsCopy);Assert.Equal(bytes,test.Dav.Objects["frozen-copy.bin"].Bytes);Assert.False(test.Dav.Objects.ContainsKey("frozen-source.bin"));
        Assert.Equal(before.Generation,test.Repository.Upload(original.Id)!.Generation);Assert.NotEqual("Completed",test.Repository.Upload(original.Id)!.State);
        Assert.DoesNotContain(test.Repository.Entries(test.Id),entry=>entry.SourcePath=="frozen-source.bin");
        var sent=Assert.Single(test.Dav.Server.Requests,request=>request.Method=="PUT"&&request.Target=="/dav/frozen-copy.bin");Assert.Equal("*",sent.Headers["If-None-Match"]);
        if(removeOriginal)Assert.False(File.Exists(path));else
        {
            Assert.Equal(later,await File.ReadAllBytesAsync(path));var native=CloudFilesNative.Inspect(path);Assert.True(native is null||native.Value.Info.InSyncState!=1);
        }
    }
    /// <summary>同长度篡改共享快照也必须由实际工作器在发送前拒绝，不能改用本地新内容或覆盖远端。</summary>
    [Fact(Timeout=120000)]
    public async Task ActualWorkerRejectsSameLengthSnapshotCorruptionBeforePut()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=await ConflictedSnapshot(test,[1,2,3]);
        await File.WriteAllBytesAsync(original.SnapshotPath!,[9,9,9]);var id=test.Manager.UploadAsCopy(original.Id,"bad-copy.bin");
        var rejected=await WaitCopy(test,id,"Conflict");Assert.True(rejected.IsCopy);Assert.False(test.Dav.Objects.ContainsKey("bad-copy.bin"));
        Assert.DoesNotContain(test.Dav.Server.Requests,request=>request.Method=="PUT"&&request.Target=="/dav/bad-copy.bin");
        Assert.Equal(new byte[]{1,2,3},await File.ReadAllBytesAsync(Path.Combine(test.Local,"frozen-source.bin")));
    }
    private static async Task<LocalUpload> ConflictedSnapshot(UploadFlowTests.Fixture test,byte[] bytes)
    {
        test.Manager.UploadCheckpointForTests=(stage,item)=>stage=="Prepared"&&!item.IsCopy&&item.LocalPath=="frozen-source.bin"
            ?Task.FromException(new SourceConflictException("隔离测试：快照后停止原上传")):Task.CompletedTask;
        await File.WriteAllBytesAsync(Path.Combine(test.Local,"frozen-source.bin"),bytes);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        var original=await test.Wait("frozen-source.bin",state:"Conflict");await test.Manager.SetSchedulingForTestsAsync(test.Id,false);test.Manager.UploadCheckpointForTests=null;
        Assert.NotNull(original.SnapshotPath);Assert.Equal(bytes,await File.ReadAllBytesAsync(original.SnapshotPath!));return original;
    }
    private static async Task<LocalUpload> WaitCopy(UploadFlowTests.Fixture test,string id,string state)
    {
        var deadline=DateTime.UtcNow.AddSeconds(30);LocalUpload? item=null;
        while(DateTime.UtcNow<deadline)
        {
            item=test.Repository.Upload(id);if(item?.State==state)return item;
            if(item?.State is "Failed" or "Conflict")throw new IOException(item.State+": "+item.Error);
            await Task.Delay(80);
        }
        throw new TimeoutException(System.Text.Json.JsonSerializer.Serialize(item));
    }
    private sealed class Dispatcher:IUiDispatcher {public bool TryEnqueue(Action action)=>true;}
    private sealed class Definitions:IMountDefinitionStore
    {
        internal readonly List<MountDefinition> Items=[];
        public long CacheLimitBytes {get;set;}=1024*1024*1024;
        public IReadOnlyList<MountDefinition> LoadDefinitions()=>Items.ToArray();
        public void SaveDefinition(MountDefinition definition){Items.RemoveAll(x=>x.Id==definition.Id);Items.Add(definition);}
        public void DeleteDefinition(Guid id)=>Items.RemoveAll(x=>x.Id==id);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly string _area=Directory.CreateTempSubdirectory("HybridMount.ConflictCopy.").FullName;
        internal Definitions Definitions {get;}=new();
        internal MountRepository Repository {get;}
        internal MountManager Manager {get;}
        private readonly LoggerService _logger;
        internal MountDefinition Mount {get;}
        internal LocalUpload Original {get;}
        internal byte[] Bytes {get;}
        internal string Snapshot {get;}
        internal string Local=>Path.Combine(Mount.LocalPath,"file");
        internal Fixture(int size)
        {
            var local=Path.Combine(_area,"mount");Directory.CreateDirectory(local);Mount=new(Guid.NewGuid(),"copy test",local,false,RootIdentity:MountPathGuard.FileIdentity(local),Source:new(FileSourceKind.WebDav,"https://example.test/dav/"),RegistrationStage:MountRegistrationStage.NotRegistered);Definitions.Items.Add(Mount);Repository=new(Path.Combine(_area,"data"),definitions:Definitions);_logger=new(Path.Combine(_area,"log.db"));Bytes=Enumerable.Repeat((byte)42,size).ToArray();File.WriteAllBytes(Local,Bytes);var stamp=LocalFileState.Read(Local);var staging=Path.Combine(Repository.DataDirectory,"UploadStaging",Mount.Id.ToString("N"));Directory.CreateDirectory(staging);Snapshot=Path.Combine(staging,"original.snapshot");File.WriteAllBytes(Snapshot,Bytes);Original=new("original",Mount.Id,"file","file",false,stamp.Key,1,"Conflict",DateTimeOffset.UtcNow,"entry","baseline",SnapshotPath:Snapshot,SnapshotSha256:Convert.ToHexString(SHA256.HashData(Bytes)),SnapshotStamp:stamp.Key,SnapshotIdentity:stamp.Identity,SnapshotGeneration:1,Total:size);Repository.SaveUpload(Original);Repository.SaveEntry(Mount.Id,new("entry","file","file","baseline",false,Protected:true,Materialized:true));Manager=new(Repository,_logger);
        }
        internal LocalUpload Copy(string destination="copy")=>Repository.Upload(Manager.UploadAsCopy(Original.Id,destination))!;
        internal void Delete(string path,bool forget=false)=>typeof(MountManager).GetMethod("DeleteUploadSnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Manager,[path,forget]);
        public async ValueTask DisposeAsync(){await Manager.DisposeAsync();await _logger.DisposeAsync();Directory.Delete(_area,true);}
    }
}
