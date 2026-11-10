using HybridMount.Models.Mounting;
using HybridMount.Models.Mounting.Recovery;
namespace HybridMount.Tests;

/// <summary>批量编排只使用内存执行器，不注册同步根或修改用户目录。</summary>
public sealed class OrphanedMountBatchTests
{
    private static OrphanedMountCandidate Root(string path)=>new(Guid.NewGuid(),path,"file-id",Guid.NewGuid().ToString(),true);
    [Fact]
    public void SelectionDeduplicatesButRejectsOverlapInvalidAndChangedIdentities()
    {
        var a=Root(@"C:\RecoveryA");var b=Root(@"C:\RecoveryB");
        Assert.Equal(2,OrphanedMountBatch.Capture([a,a,b]).Length);
        Assert.Throws<IOException>(()=>OrphanedMountBatch.Capture([a,Root(@"C:\RecoveryA\child")]));
        Assert.Throws<IOException>(()=>OrphanedMountBatch.Capture([a,a with{RootIdentity="changed"}]));
        Assert.Throws<IOException>(()=>OrphanedMountBatch.Capture([a with{Error="not owned"}]));
        Assert.Throws<InvalidOperationException>(()=>OrphanedMountBatch.Capture([]));
    }
    [Fact]
    public async Task FailureContinuesAndExecutionRemainsSerialWithExactItemResults()
    {
        var roots=new[]{Root(@"C:\RecoveryA"),Root(@"C:\RecoveryB"),Root(@"C:\RecoveryC")};
        var active=0;var maximum=0;var paths=new List<string>();var observed=new List<OrphanedMountBatchProgress>();
        var result=await OrphanedMountBatch.RunAsync(roots,async(root,token,report)=>
        {
            maximum=Math.Max(maximum,Interlocked.Increment(ref active));await Task.Yield();paths.Add(root.LocalPath);Interlocked.Decrement(ref active);
            if(root==roots[1])throw new IOException("locked");report("Completed");return new(true,3,"Completed",[]);
        },default,observed.Add);
        Assert.Equal(1,maximum);Assert.Equal(roots.Select(x=>x.LocalPath),paths);Assert.Equal(2,result.Completed);Assert.Equal(1,result.Failed);
        Assert.NotNull(result.Items[1].HResult);Assert.Contains("locked",result.Detail);Assert.Equal(3,observed.Max(x=>x.Index));
    }
    [Fact]
    public async Task CancelStopsFutureRootsAndKeepsCompletedResults()
    {
        using var cancel=new CancellationTokenSource();var calls=0;
        var roots=Enumerable.Range(0,4).Select(x=>Root(@"C:\Recovery"+x)).ToArray();
        var result=await OrphanedMountBatch.RunAsync(roots,(root,token,report)=>
        {
            calls++;if(calls==1)return Task.FromResult(new OrphanedMountResult(true,2,"Completed",[]));
            cancel.Cancel();token.ThrowIfCancellationRequested();throw new InvalidOperationException();
        },cancel.Token);
        Assert.Equal(2,calls);Assert.Equal(1,result.Completed);Assert.Equal(1,result.Canceled);Assert.Equal(2,result.NotStarted);
    }
}
