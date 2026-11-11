using System.Security.Cryptography;
using System.Text;
using HybridMount.Commons;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;
/// <summary>生产.63重传链回归：远端回执保留、锁外同内容证明与真实后续正文分开处理；仅隔离CFAPI根和回环服务。</summary>
[Collection("Native IPC")]
public sealed class UploadReceiptRetentionTests
{
    private static int Writes(UploadFlowTests.Fixture test,string name)=>test.Dav.Server.Requests.Count(x=>x.Method=="PUT"&&Uri.UnescapeDataString(new Uri(test.Dav.Server.Address,x.Target).AbsolutePath).EndsWith("/"+name,StringComparison.Ordinal));
    [Theory(Timeout=60000)][InlineData(false)][InlineData(true)]
    public async Task MetadataOnlyChangeAfterRemoteReceiptDoesNotSendBodyAgain(bool advanceGeneration)
    {
        await using var test=await UploadFlowTests.Fixture.Create(legacyPreviews:false);var file=Path.Combine(test.Local,"metadata.bin");var bytes=Encoding.UTF8.GetBytes("verified same content");var changed=0;
        test.Manager.UploadCheckpointForTests=(stage,item)=>
        {
            if(stage=="RemoteCommitted"&&item.LocalPath=="metadata.bin"&&Interlocked.Exchange(ref changed,1)==0){File.SetLastWriteTimeUtc(file,File.GetLastWriteTimeUtc(file).AddMinutes(1));if(advanceGeneration){var stamp=LocalFileState.Read(file);test.Repository.UpdateUpload(item.Id,current=>current with{Generation=current.Generation+1,ObservedStamp=stamp.Key});}}
            return Task.CompletedTask;
        };
        await File.WriteAllBytesAsync(file,bytes);var completed=await test.Wait("metadata.bin",bytes:bytes);Assert.Equal(1,Writes(test,"metadata.bin"));Assert.Equal(1,changed);Assert.Equal(advanceGeneration?2:1,completed.Generation);Assert.Equal(1u,CloudFilesNative.Inspect(file)!.Value.Info.InSyncState);Assert.Equal(bytes,await File.ReadAllBytesAsync(file));
    }
    [Fact(Timeout=60000)]
    public async Task VerifiedDifferentBodyWithSameLengthAndTimeCreatesExactlyOneLaterUpload()
    {
        await using var test=await UploadFlowTests.Fixture.Create(legacyPreviews:false);var file=Path.Combine(test.Local,"new-content.bin");var first=Encoding.UTF8.GetBytes("first-body");var second=Encoding.UTF8.GetBytes("other-body");var changed=0;
        test.Manager.UploadCheckpointForTests=(stage,item)=>{if(stage=="RemoteCommitted"&&item.LocalPath=="new-content.bin"&&Interlocked.Exchange(ref changed,1)==0){var time=File.GetLastWriteTimeUtc(file);File.WriteAllBytes(file,second);File.SetLastWriteTimeUtc(file,time);var stamp=LocalFileState.Read(file);test.Repository.UpdateUpload(item.Id,current=>current with{Generation=current.Generation+1,ObservedStamp=stamp.Key});}return Task.CompletedTask;};
        await File.WriteAllBytesAsync(file,first);var completed=await test.Wait("new-content.bin",bytes:second);Assert.Equal(2,Writes(test,"new-content.bin"));Assert.True(completed.Generation>=2);Assert.Equal(second,await File.ReadAllBytesAsync(file));
    }
    [Fact(Timeout=60000)]
    public async Task MissingLocalCommitProofRetainsReceiptAndSnapshotAndManualRetryOnlyCommitsLocally()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.DisposeAsync();var committer=new SwitchCommitter();test.Manager=new(test.Repository,test.Logger,uploadCommitter:committer){LegacyPreviewsForTests=false};await test.Manager.ConnectAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"pending.bin"),"receipt must survive");var until=DateTime.UtcNow.AddSeconds(25);LocalUpload? pending=null;
        while(DateTime.UtcNow<until){pending=test.Repository.Uploads(test.Id).FirstOrDefault(x=>x.LocalPath=="pending.bin");if(pending is {State:"RemoteCommitted",TransientFailureCount:3,NextAttemptUtc:null})break;await Task.Delay(50);}
        Assert.NotNull(pending);Assert.Equal("RemoteCommitted",pending.State);Assert.Equal(3,pending.TransientFailureCount);Assert.NotNull(pending.Receipt);Assert.NotNull(pending.SnapshotPath);Assert.True(File.Exists(pending.SnapshotPath));Assert.Equal(pending.Total,pending.Bytes);Assert.Equal(1,Writes(test,"pending.bin"));
        committer.Allow=true;Assert.True(test.Manager.RetryUpload(pending.Id));await test.Wait("pending.bin");Assert.Equal(1,Writes(test,"pending.bin"));
    }
    private sealed class SwitchCommitter:ILocalUploadCommitter,IRecoveryUploadCommitter
    {
        internal volatile bool Allow;private readonly CloudUploadCommitter _native=new();
        public LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared)=>Allow?_native.Commit(mount,entry,prepared):null;
        public bool NeedsRecovery(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp stamp)=>_native.NeedsRecovery(mount,entry,prepared,stamp);
        public LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp? verifiedStamp)=>Allow?_native.Commit(mount,entry,prepared,verifiedStamp):null;
    }
}
