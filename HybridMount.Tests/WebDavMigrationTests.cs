using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>DavClient 迁移中有风险的正文超时、MOVE 207 和更新 LOCK 竞态。</summary>
public sealed class WebDavMigrationTests
{
    [Theory(Timeout=10000)]
    [InlineData(false)][InlineData(true)]
    public async Task MetadataDeadlineIncludesBodyAndCallerCancellationRemainsCancellation(bool callerCancel)
    {
        await using var fixture=new DavFixture();var original=fixture.Server.Handler;var headers=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Handler=async request=>{var result=await original(request);return request.Method=="PROPFIND"?result with{BeforeBody=new TaskCompletionSource().Task,HeadersSent=headers}:result;};
        await using var source=fixture.Source();source.MetadataDeadline=TimeSpan.FromMilliseconds(callerCancel?5000:120);
        using var stop=new CancellationTokenSource();var read=source.GetMetadataAsync("文件.bin",stop.Token);await headers.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if(callerCancel){stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>read);}else await Assert.ThrowsAsync<IOException>(()=>read);
    }
    [Theory(Timeout=10000)]
    [InlineData(false)][InlineData(true)]
    public async Task RangeAndSharedSnapshotBodyHaveIdleTimeout(bool weak)
    {
        await using var fixture=new DavFixture{WeakEtag=weak};var original=fixture.Server.Handler;
        fixture.Server.Handler=async request=>{var result=await original(request);return request.Method=="GET"?result with{BeforeBody=new TaskCompletionSource().Task}:result;};
        await using var source=fixture.Source();source.ReadIdleTimeout=TimeSpan.FromMilliseconds(100);var entry=await source.GetMetadataAsync("文件.bin");
        var error=await Assert.ThrowsAsync<IOException>(async()=>await source.ReadRangeAsync(entry,0,new byte[8]));Assert.Contains("正文",error.Message);
    }
    [Fact]
    public async Task Move207WithLockedChildReportsPartialMoveAndKeepsSource()
    {
        await using var fixture=new DavFixture();fixture.Objects["folder"]=new([],"\"folder\"",true);fixture.Put("folder/locked",[1]);var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="MOVE"&&request.Target=="/dav/folder"?Task.FromResult(SyncDavFixture.Multi("urn:irrelevant",[SyncDavFixture.Resource("/dav/folder/locked",423)])):original(request);
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("folder");
        var error=await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync("folder","destination",entry.Version));
        Assert.Contains(error.Results,x=>x.Path=="folder/locked"&&x.Status=="Failed");Assert.True(fixture.Objects.ContainsKey("folder/locked"));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DeletedUpdateTargetIsNotResurrectedByFinalLock(bool ignoreFinalCondition)
    {
        await using var fixture=new DavLockFixture{RawTags=true};fixture.Dav.IgnoreConditions=true;fixture.Dav.Put("update.bin",[1,2,3]);var original=fixture.Dav.Server.Handler;
        fixture.Dav.Server.Handler=request=>
        {
            if(request.Method=="LOCK"&&request.Target=="/dav/update.bin")
            {
                Assert.Equal("*",request.Headers.GetValueOrDefault("If-Match"));fixture.Dav.Objects.TryRemove("update.bin",out _);
                if(ignoreFinalCondition){var headers=new Dictionary<string,string>(request.Headers,StringComparer.OrdinalIgnoreCase);headers.Remove("If-Match");request=request with{Headers=headers};}
            }
            return original(request);
        };
        await using var source=fixture.Dav.Source();var entry=await source.GetMetadataAsync("update.bin");var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[]{1,2,3}));
        var error=await Record.ExceptionAsync(()=>source.UploadWithPreconditionAsync("update.bin",new MemoryStream([7,8,9]),new(entry.Version,hash)));
        Assert.True(error is SourceConflictException or NotSupportedException,error?.ToString());Assert.False(fixture.Dav.Objects.ContainsKey("update.bin"));Assert.Empty(fixture.Locks);
    }
}
