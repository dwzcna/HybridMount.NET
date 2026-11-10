using System.Diagnostics;
using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Tests;

/// <summary>同一原生父进程复用纯C++解码子进程，实际等待30秒空闲退出并核验无源读取。</summary>
[Collection("Native IPC")]
public sealed class PersistentLocalDecoderTests
{
    [Fact(Timeout=90000)]
    public async Task LocalDecoderReusesOneProcessProducesFourFramesAndExitsAfterThirtyIdleSeconds()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var bytes=PreviewPipelineTests.Bmp();test.Dav.Put("resident.bmp",bytes);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var entry=test.Repository.Entries(test.Id).Single(item=>item.LocalPath=="resident.bmp");var path=Path.Combine(test.Local,entry.LocalPath);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));
        await test.Manager.DisconnectAsync(test.Id);var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);var before=CloudFilesNative.InspectPersisted(path);var requests=test.Dav.Server.Requests.Count;
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--test-local-thumbnail-reader");using var process=Process.Start(start)!;var errors=process.StandardError.ReadToEndAsync();
        async Task<JsonElement> Request(object packet){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));await IpcProtocol.WriteAsync(process.StandardInput.BaseStream,packet,timeout.Token);return await IpcProtocol.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,timeout.Token);}
        try
        {
            var first=await Request(new{kind="decode",path});Assert.True(first.GetProperty("hit").GetBoolean(),first.GetRawText());Assert.True(first.GetProperty("started").GetBoolean());var pid=first.GetProperty("decoder").GetProperty("pid").GetInt32();Assert.True(pid>0);
            Assert.Equal(4,first.GetProperty("frames").EnumerateObject().Count());foreach(var size in PreviewWorker.Sizes){var png=first.GetProperty("frames").GetProperty(size.ToString()).GetBytesFromBase64();Assert.InRange(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)),1,size);}
            var second=await Request(new{kind="decode",path});Assert.True(second.GetProperty("hit").GetBoolean(),second.GetRawText());Assert.False(second.GetProperty("started").GetBoolean());Assert.Equal(pid,second.GetProperty("decoder").GetProperty("pid").GetInt32());
            using(var child=Process.GetProcessById(pid))Assert.DoesNotContain(child.Modules.Cast<ProcessModule>(),module=>module.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase));
            // 成功和格式失败后进程继续存活，但不得继续持有本项的普通文件句柄；替换须立即可用。
            var regular=Path.Combine(test.Local,"ordinary-resident.bmp");await File.WriteAllBytesAsync(regular,bytes);
            var ordinary=await Request(new{kind="decode",path=regular});Assert.True(ordinary.GetProperty("hit").GetBoolean(),ordinary.GetRawText());Assert.Equal(pid,ordinary.GetProperty("decoder").GetProperty("pid").GetInt32());
            using(var exclusive=File.OpenHandle(regular,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Assert.False(exclusive.IsInvalid);
            var invalid=Path.Combine(test.Local,"invalid-resident.bmp");await File.WriteAllBytesAsync(invalid,[1,2,3,4]);var failed=await Request(new{kind="decode",path=invalid});Assert.False(failed.GetProperty("hit").GetBoolean());
            File.Move(invalid,invalid+".moved");Assert.Equal(pid,(await Request(new{kind="statistics"})).GetProperty("decoder").GetProperty("pid").GetInt32());
            await Task.Delay(TimeSpan.FromSeconds(31));var idle=await Request(new{kind="statistics"});Assert.Equal(0,idle.GetProperty("decoder").GetProperty("pid").GetInt32());Assert.Equal(1,idle.GetProperty("decoder").GetProperty("idleExits").GetInt32());
            var after=CloudFilesNative.InspectPersisted(path);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Null(store.Binding(path));
            await IpcProtocol.WriteAsync(process.StandardInput.BaseStream,new{kind="stop"});await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));Assert.Equal(0,process.ExitCode);Assert.Empty(await errors);
        }
        finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}store.DeleteDatabase(store.Root(test.Id));}
    }
}
