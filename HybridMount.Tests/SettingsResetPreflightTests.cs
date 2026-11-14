using HybridMount.Services;
using HybridMount.Services.Shell;

namespace HybridMount.Tests;

/// <summary>预期拒绝不扫描或删除数据；维护所有者仅运行内存动作，不访问现场注册。</summary>
public sealed class SettingsResetPreflightTests
{
    private static SettingsResetScope Scope()=>SettingsResetScope.Create(false,Path.Combine(Path.GetTempPath(),"ResetPreflight"));
    [Fact]
    public void RegistrationBlockIsAResultWithoutCapturingDeletionProofs()
    {
        var reads=0;var failures=0;var observingThread=Environment.CurrentManagedThreadId;
        void Observe(object? _,System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e){if(Environment.CurrentManagedThreadId==observingThread&&e.Exception is IOException)failures++;}
        AppDomain.CurrentDomain.FirstChanceException+=Observe;
        try
        {
            var result=SettingsResetPreflight.Inspect(Scope(),()=>"leftover registration",()=>{reads++;return [];},()=>reads++);
            Assert.Null(result.Plan);Assert.Equal("leftover registration",result.BlockedReason);Assert.Equal(0,reads);Assert.Equal(0,failures);
        }
        finally{AppDomain.CurrentDomain.FirstChanceException-=Observe;}
    }
    [Fact]
    public void RealQueryFailuresRemainErrorsAndDoNotAuthorizeReset()
    {
        Assert.Throws<UnauthorizedAccessException>(()=>SettingsResetPreflight.Inspect(Scope(),()=>throw new UnauthorizedAccessException(),()=>[],()=>{}));
        Assert.Throws<IOException>(()=>SettingsResetPreflight.Inspect(Scope(),()=>null,()=>throw new IOException("disk error"),()=>{}));
    }
    [Fact]
    public void SuccessfulPreflightRetainsExactCurrentModeProof()
    {
        var scope=Scope();var proofs=new[]{new SettingsResetDirectoryProof(scope.Directories[0],"identity")};var verified=false;
        var result=SettingsResetPreflight.Inspect(scope,()=>null,()=>proofs,()=>verified=true);
        Assert.Null(result.BlockedReason);Assert.Same(scope,result.Plan!.Scope);Assert.Same(proofs,result.Plan.Directories);Assert.True(verified);
    }
    [Fact]
    public async Task MaintenanceStopDrainsCurrentWorkAndRejectsQueuedWork()
    {
        var owner=new ExplorerEntryMaintenance();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var queuedRan=false;
        var first=owner.RunAsync(token=>{entered.Set();release.Wait(TimeSpan.FromSeconds(5));token.ThrowIfCancellationRequested();return 1;},default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var queued=owner.RunAsync(_=>{queuedRan=true;return 2;},default);
        var stop=owner.DisposeAsync().AsTask();
        await Task.Delay(30);Assert.False(stop.IsCompleted);release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>queued);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(queuedRan);
        Assert.Throws<OperationCanceledException>(()=>{_ = owner.RunAsync(_=>3,default);});owner.RequestStop();await owner.DisposeAsync();
    }
}
