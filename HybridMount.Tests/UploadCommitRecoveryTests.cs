using System.Security.Cryptography;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>隔离文件与仓库回归：摘要期间根锁可用，变更、取消和迟到结果不能覆盖当前提交状态。</summary>
public sealed class UploadCommitRecoveryTests
{
    [Fact(Timeout=20000)]
    public async Task ActualHashReleasesGateAndPublishesVerifiedStamp()
    {
        using var test=new Fixture(3*1024*1024);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var paused=false;
        var operation=test.Run(async()=>{if(!paused){paused=true;entered.SetResult();await release.Task;}});
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(await test.Gate.WaitAsync(TimeSpan.FromSeconds(2)));test.Gate.Release();}finally{release.TrySetResult();}
        var result=await operation;Assert.NotNull(result);Assert.Equal(test.Stamp.Key,result.VerifiedStamp!.Key);Assert.True(test.Gate.Wait(0));test.Gate.Release();
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(262145)]
    public async Task NormalAndEmptyRecoveryVerifyActualContent(int size)
    {
        using var test=new Fixture(size);var result=await test.Run();Assert.NotNull(result);Assert.Equal(test.Stamp.Key,result.VerifiedStamp!.Key);
        Assert.True(CloudUploadState.MatchesRecovery(test.Stamp,result.VerifiedStamp,true));Assert.False(CloudUploadState.MatchesRecovery(test.Stamp,result.VerifiedStamp,false));
    }
    [Theory]
    [InlineData("generation")][InlineData("state")][InlineData("attempt")][InlineData("receipt")][InlineData("cancel")][InlineData("edit")][InlineData("replace")][InlineData("delete")]
    public async Task ChangesDuringHashDiscardProofAndPreserveCurrentRow(string change)
    {
        using var test=new Fixture(262145);var changed=false;LocalUpload? expected=null;
        var result=await test.Run(()=>
        {
            if(changed)return Task.CompletedTask;changed=true;
            switch(change)
            {
                case "edit":File.WriteAllBytes(test.Path,[9]);break;
                case "replace":File.Delete(test.Path);File.WriteAllBytes(test.Path,[9]);break;
                case "delete":File.Delete(test.Path);break;
                default:test.Row=change switch{"generation"=>test.Row with{Generation=2},"state"=>test.Row with{State="Conflict"},"attempt"=>test.Row with{SnapshotAttemptId="new"},"receipt"=>test.Row with{Receipt=new("","file",false,1,DateTimeOffset.UtcNow,"new")},_=>test.Row with{CancelRequested=true}};break;
            }
            expected=test.Row;return Task.CompletedTask;
        });
        Assert.Null(result);Assert.Equal(expected,test.Row);
    }
    [Fact]
    public async Task DigestMismatchCannotAuthorizeNativeRecovery()
    {
        using var test=new Fixture(1);test.Row=test.Row with{SnapshotSha256=new string('0',64)};var result=await test.Run();Assert.NotNull(result);Assert.Null(result.VerifiedStamp);Assert.False(CloudUploadState.MatchesRecovery(test.Stamp,result.VerifiedStamp,true));
    }
    [Fact]
    public void NativeRecheckRejectsChangesAfterVerification()
    {
        using var test=new Fixture(1);Assert.False(CloudUploadState.MatchesRecovery(test.Stamp with{Usn=test.Stamp.Usn+1},test.Stamp,true));Assert.False(CloudUploadState.MatchesRecovery(test.Stamp,null,true));
    }
    [Fact]
    public async Task NormalCommitDoesNotHash()
    {
        using var test=new Fixture(1);var result=await UploadCommitRecovery.VerifyAsync(test.Gate,()=>new(test.Row,test.Stamp,false),_=>true,_=>throw new InvalidOperationException(),default);Assert.NotNull(result);Assert.Null(result.VerifiedStamp);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ErrorOrCancellationLeavesGateAndRowIntact(bool cancel)
    {
        using var test=new Fixture(262145);using var stop=new CancellationTokenSource();
        var operation=test.Run(()=>{if(cancel){stop.Cancel();stop.Token.ThrowIfCancellationRequested();}throw new IOException("hash failed");},stop.Token);
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>operation);else await Assert.ThrowsAsync<IOException>(()=>operation);
        Assert.Equal(test.Original,test.Row);Assert.True(test.Gate.Wait(0));test.Gate.Release();
    }
    [Fact]
    public async Task StaleFailureDoesNotReachPublisher()
    {
        using var test=new Fixture(1);var result=await test.Run(()=>{test.Row=test.Row with{Generation=2};throw new IOException("late failure");});Assert.Null(result);Assert.Equal(2,test.Row.Generation);Assert.Null(test.Row.Error);
    }
    [Theory]
    [InlineData("happy")][InlineData("edit")][InlineData("row")][InlineData("late-error")][InlineData("transaction")]
    public async Task PublicationKeepsCommitUnderGateAndRejectsStaleHashAndTransaction(string change)
    {
        using var files=new Fixture(262145);var repository=new MountRepository(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(files.Path)!,"data"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));
        var receipt=new SourceEntry("","file",false,262145,DateTimeOffset.UtcNow,"remote");var item=files.Original with{SnapshotStamp=(files.Stamp with{Usn=files.Stamp.Usn-1}).Key,Receipt=receipt};repository.SaveUpload(item);var hashed=false;LocalUpload? committed=null;var calls=0;
        bool Current(UploadCommitCapture capture)=>repository.Upload(item.Id)==capture.Row&&UploadCommitRecovery.SameStamp(files.Path,capture.Stamp!);
        var recovery=await UploadCommitRecovery.VerifyAsync(files.Gate,()=>new(repository.Upload(item.Id)!,files.Stamp,true),Current,capture=>UploadCommitRecovery.HashAsync(files.Path,files.Stamp,default,()=>
        {
            if(!hashed)
            {
                hashed=true;Assert.True(files.Gate.Wait(0));files.Gate.Release();
                if(change=="edit")File.WriteAllBytes(files.Path,[9]);
                if(change is "row" or "late-error")repository.UpdateUpload(item.Id,current=>current with{Generation=2,SnapshotAttemptId="new-attempt"});
                if(change=="late-error")throw new IOException("late hash error");
            }
            return Task.CompletedTask;
        }),default);
        if(recovery is not null)
        {
            await files.Gate.WaitAsync();
            try
            {
                Assert.True(Current(recovery));Assert.Equal(0,files.Gate.CurrentCount);Assert.True(CloudUploadState.MatchesRecovery(files.Stamp,recovery.VerifiedStamp,true));calls++;
                if(change=="transaction")repository.UpdateUpload(item.Id,current=>current with{Generation=2});
                var entry=new MountEntry(item.Id,"file","file",receipt.Version,false,Materialized:true);
                committed=repository.TryCommitUploadedEntry(recovery.Row,item,entry,receipt,recovery.VerifiedStamp);
            }
            finally{files.Gate.Release();}
        }
        Assert.True(hashed);Assert.Equal(change=="happy",committed is not null);Assert.Equal(change is "happy" or "transaction"?1:0,calls);
        var current=repository.Upload(item.Id)!;Assert.Equal(change=="happy"?"Completed":"RemoteCommitted",current.State);Assert.Null(current.Error);Assert.Equal(change is "row" or "late-error" or "transaction"?2:1,current.Generation);
    }
    private sealed class Fixture:IDisposable
    {
        private readonly string _area=Directory.CreateTempSubdirectory("HybridMount.CommitHash.").FullName;
        internal string Path=>System.IO.Path.Combine(_area,"file");
        internal SemaphoreSlim Gate {get;}=new(1,1);
        internal LocalFileStamp Stamp {get;}
        internal LocalUpload Original {get;}
        internal LocalUpload Row {get;set;}
        internal Fixture(int size)
        {
            var bytes=new byte[size];File.WriteAllBytes(Path,bytes);Stamp=LocalFileState.Read(Path);
            Original=new("one",Guid.NewGuid(),"file","file",false,"snapshot-stamp",1,"RemoteCommitted",DateTimeOffset.UtcNow,SnapshotIdentity:Stamp.Identity,SnapshotStamp:"snapshot-stamp",SnapshotGeneration:1,SnapshotSha256:Convert.ToHexString(SHA256.HashData(bytes)));Row=Original;
        }
        internal Task<UploadCommitCapture?> Run(Func<Task>? checkpoint=null,CancellationToken token=default)=>UploadCommitRecovery.VerifyAsync(Gate,()=>new(Row,Stamp,true),capture=>
        {
            Assert.Equal(0,Gate.CurrentCount);return Row==capture.Row&&File.Exists(Path)&&LocalFileState.Read(Path).Key==capture.Stamp!.Key;
        },capture=>UploadCommitRecovery.HashAsync(Path,capture.Stamp!,token,checkpoint),token);
        public void Dispose(){Gate.Dispose();Directory.Delete(_area,true);}
    }
}
