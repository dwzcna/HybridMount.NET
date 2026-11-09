using System.Diagnostics;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;
/// <summary>独占原生IPC集合，故障停止和显式重连仅操作本轮隔离根。</summary>
[Collection("Native IPC")]
public sealed class NativeRuntimeFailureTests
{
    [Fact(Timeout=90000)]
    public async Task NativeCrashStopsMountButKeepsRegistrationAndExplicitReconnectCreatesNewGeneration()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var mount=test.Manager.Mounts.Single().Definition;var old=NativeRuntimeClient.Get();
        var oldProcessId=old.ProcessId;using(var process=Process.GetProcessById(oldProcessId)){var modules=process.Modules.Cast<ProcessModule>().Select(item=>item.ModuleName);Assert.DoesNotContain(modules,item=>item.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase));process.Kill();await process.WaitForExitAsync();}
        var until=DateTime.UtcNow.AddSeconds(10);while(test.Manager.Mounts.Single().Status!="原生后台已断开"&&DateTime.UtcNow<until)await Task.Delay(25);
        Assert.Equal("原生后台已断开",test.Manager.Mounts.Single().Status);Assert.Single(test.Repository.LoadDefinitions());await using(var inspector=await NativeRuntimeClient.StartOwnedForTestsAsync()){var state=await inspector.RootInfoAsync(test.Local);Assert.True(state.GetProperty("hresult").GetInt32()>=0);Assert.Equal(mount.Id,System.Text.Json.JsonSerializer.Deserialize<MountPathGuard.RootMarker>(state.GetProperty("identity").GetBytesFromBase64())!.MountId);}
        await test.Manager.ConnectAsync(test.Id);Assert.Equal("已连接",test.Manager.Mounts.Single().Status);Assert.NotEqual(oldProcessId,NativeRuntimeClient.Get().ProcessId);Assert.Equal(mount.RootIdentity,MountPathGuard.FileIdentity(test.Local));
        Assert.Equal(test.Dav.Objects["文件.bin"].Bytes,await File.ReadAllBytesAsync(Path.Combine(test.Local,"文件.bin")));
    }
    /// <summary>真实后台崩溃后批量重连两个隔离根，覆盖手动根、用户断开优先及单根协议失败继续。</summary>
    [Theory(Timeout=90000)]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)]
    public async Task BatchReconnectHonorsManualRootsUserDisconnectAndIndependentFailures(bool disconnectFirst,bool failFirst)
    {
        BatchFactory factory=null!;await using var test=await UploadFlowTests.Fixture.Create(false,sourceFactory:_=>factory=new BatchFactory(),legacyPreviews:false);var secondPath=Path.Combine(test.Dav.Area,"second-view");Directory.CreateDirectory(secondPath);var second=await test.Manager.AddAsync("manual root",secondPath,false,source:test.Dav.Options,credentials:new("user","app-password"),autoUpload:false);
        try
        {
            Assert.False(second.AutoConnect);var firstIdentity=MountPathGuard.FileIdentity(test.Local);var secondIdentity=MountPathGuard.FileIdentity(secondPath);var old=NativeRuntimeClient.Get();var pid=old.ProcessId;using(var process=Process.GetProcessById(pid)){process.Kill();await process.WaitForExitAsync();}
            var until=DateTime.UtcNow.AddSeconds(10);while(!test.Manager.HasRuntimeFailedMounts&&DateTime.UtcNow<until)await Task.Delay(20);Assert.True(test.Manager.HasRuntimeFailedMounts);
            if(disconnectFirst)await test.Manager.DisconnectAsync(test.Id);if(failFirst)factory.Fail=test.Id;
            await Task.WhenAll(test.Manager.ReconnectRuntimeFailedAsync(),test.Manager.ReconnectRuntimeFailedAsync());var rows=test.Manager.Mounts.ToDictionary(x=>x.Definition.Id);
            Assert.Equal(!disconnectFirst&&!failFirst,rows[test.Id].IsConnected);Assert.True(rows[second.Id].IsConnected);Assert.Equal(failFirst,test.Manager.HasRuntimeFailedMounts);if(failFirst){factory.Fail=null;await test.Manager.ReconnectRuntimeFailedAsync();Assert.True(test.Manager.Mounts.Single(x=>x.Definition.Id==test.Id).IsConnected);Assert.False(test.Manager.HasRuntimeFailedMounts);}Assert.NotEqual(pid,NativeRuntimeClient.Get().ProcessId);Assert.Equal(firstIdentity,MountPathGuard.FileIdentity(test.Local));Assert.Equal(secondIdentity,MountPathGuard.FileIdentity(secondPath));Assert.Equal(test.Dav.Objects["文件.bin"].Bytes,await File.ReadAllBytesAsync(Path.Combine(secondPath,"文件.bin")));
        }
        finally{factory.Fail=null;await WebDavTests.Finished(test.Manager,test.Manager.Queue(second.Id,"clearAndRemove"));}
    }
    private sealed class BatchFactory:IFileSourceFactory
    {
        internal Guid? Fail;
        public Task<HybridMount.Commons.IFileSource> CreateAsync(HybridMount.Models.Mounting.MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)=>mount.Id==Fail?Task.FromException<HybridMount.Commons.IFileSource>(new IOException("isolated reconnect failure")):new FileSourceFactory().CreateAsync(mount,repository,recovery,token);
    }
    /// <summary>真实NTFS移出／跨根拒绝，说明与原权限结果同时存在，原路径和内容状态保持。</summary>
    [Theory(Timeout=90000)]
    [InlineData(false)][InlineData(true)]
    public async Task RejectedMovePublishesExplanationWithoutChangingTheFile(bool acrossRoot)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);HybridMount.Models.Mounting.MountDefinition? second=null;var targetDirectory=Path.Combine(test.Dav.Area,"move-target");Directory.CreateDirectory(targetDirectory);if(acrossRoot)second=await test.Manager.AddAsync("other root",targetDirectory,false,source:test.Dav.Options,credentials:new("user","app-password"),autoUpload:false);var path=Path.Combine(test.Local,"文件.bin");var target=Path.Combine(targetDirectory,"moved.bin");var before=LocalFileState.Read(path);var requests=test.Dav.Server.Requests.Count;var warning=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);test.Manager.RenameRejected+=(id,message)=>{if(id==test.Id)warning.TrySetResult(message);};
        try{Assert.NotNull(Record.Exception(()=>File.Move(path,target)));var message=await warning.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Contains("复制后再删除",message);Assert.DoesNotContain(test.Dav.Area,message);Assert.True(File.Exists(path));Assert.False(File.Exists(target));Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);Assert.Equal(requests,test.Dav.Server.Requests.Count);}
        finally{if(second is not null)await WebDavTests.Finished(test.Manager,test.Manager.Queue(second.Id,"clearAndRemove"));}
    }
}
