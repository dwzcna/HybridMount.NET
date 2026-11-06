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
        await using var test=new Fixture(1);test.Repository.UpdateUpload(test.Original.Id,x=>x with{SnapshotPath=null,SnapshotSha256=null,SnapshotStamp=null,SnapshotIdentity=null,SnapshotGeneration=0});var copy=test.Copy();Assert.Null(copy.SnapshotPath);Assert.Equal("Queued",copy.State);using var vm=new TasksViewModel(test.Manager,new Dispatcher());Assert.Contains("没有快照时",vm.CopyContentNotice);Assert.Contains("上传准备时的当前本地内容",vm.CopyContentNotice);Assert.Contains("不是冲突时的内容",vm.CopyContentNotice);
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
