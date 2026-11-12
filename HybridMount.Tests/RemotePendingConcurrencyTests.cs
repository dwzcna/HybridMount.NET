using HybridMount.Models.Mounting;
namespace HybridMount.Tests;

/// <summary>纯SQLite隔离目录验证扫描和本地提交的原子状态屏障；不连接CFAPI、不读正式配置。</summary>
public sealed class RemotePendingConcurrencyTests
{
    private sealed class Definitions:IMountDefinitionStore
    {
        public long CacheLimitBytes {get;set;}=0;
        public IReadOnlyList<MountDefinition> LoadDefinitions()=>Array.Empty<MountDefinition>();
        public void SaveDefinition(MountDefinition value){}
        public void DeleteDefinition(Guid id){}
    }
    [Fact]
    public void NewScanCannotBeUndoneByLatePreparationOrNativeCommit()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.PendingConcurrency",Guid.NewGuid().ToString("N"));
        try
        {
            var repository=new MountRepository(area,definitions:new Definitions());var mount=Guid.NewGuid();
            var old=new RemotePendingChange("old",mount,"scope","file.bin",new("file","file.bin",false,3,DateTimeOffset.UtcNow,"v1"),"Downloading");repository.SaveRemotePending(old);
            repository.CommitRemoteBatch(mount,"scope",new("",Mode:"Full"),[new(new("file","file.bin",false,4,DateTimeOffset.UtcNow,"v2"))],[]);
            Assert.Equal("Superseded",repository.SaveRemotePendingCurrent(old with{State="Prepared",Sha256="old-hash"}).State);
            Assert.Throws<SourceConflictException>(()=>repository.CommitRemotePlaceholder(old with{State="Committing"},null));
            Assert.Equal("Superseded",repository.FindRemotePending(old.Id)!.State);Assert.Single(repository.AllRemotePending(mount),item=>item.State=="Pending"&&item.Entry?.Version=="v2");
        }
        finally{if(Directory.Exists(area))Directory.Delete(area,true);}
    }
    [Fact]
    public void VerifiedFingerprintCanRefineAListedVersionButCannotCommitTheUnrefinedOne()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.PendingFingerprint",Guid.NewGuid().ToString("N"));
        try
        {
            var repository=new MountRepository(area,definitions:new Definitions());var mount=Guid.NewGuid();
            var listed=new RemotePendingChange("listed",mount,"scope","file.bin",new("file","file.bin",false,3,DateTimeOffset.UtcNow,"hint"));repository.SaveRemotePending(listed);
            var verified=listed with{Entry=listed.Entry! with{Version="hint;md5:verified"}};
            Assert.Equal(verified,repository.SaveRemotePendingCurrent(verified));
            Assert.Throws<SourceConflictException>(()=>repository.CommitRemotePlaceholder(listed with{State="Committing"},null));
        }
        finally{if(Directory.Exists(area))Directory.Delete(area,true);}
    }
    [Fact]
    public void PersistedCommitIntentSurvivesNewScanUntilItsOwnSafeCompletion()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.PendingCommit",Guid.NewGuid().ToString("N"));
        try
        {
            var repository=new MountRepository(area,definitions:new Definitions());var mount=Guid.NewGuid();
            var old=new RemotePendingChange("old",mount,"scope","file.bin",new("file","file.bin",false,3,DateTimeOffset.UtcNow,"v1"));repository.SaveRemotePending(old);
            Assert.Equal("Committing",repository.SaveRemotePendingCurrent(old with{State="Committing"}).State);
            repository.CommitRemoteBatch(mount,"scope",new("",Mode:"Full"),[new(new("file","file.bin",false,4,DateTimeOffset.UtcNow,"v2"))],[]);
            Assert.Equal("Committing",repository.FindRemotePending(old.Id)!.State);repository.CommitRemotePlaceholder(old with{State="Completed"},null);
            Assert.Equal("Completed",repository.FindRemotePending(old.Id)!.State);Assert.Single(repository.AllRemotePending(mount),item=>item.State=="Pending"&&item.Entry?.Version=="v2");
        }
        finally{if(Directory.Exists(area))Directory.Delete(area,true);}
    }
}
