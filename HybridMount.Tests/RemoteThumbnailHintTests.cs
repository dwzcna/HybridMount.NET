using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
namespace HybridMount.Tests;

/// <summary>隔离命名管道复用公共v2请求；真实冷占位只读取有界源片段，不连接正式应用。</summary>
[Collection("Native IPC")]
public sealed class RemoteThumbnailHintTests
{
    [Theory(Timeout=60000)][InlineData("ready")][InlineData("absent")][InlineData("wrongPeer")][InlineData("stalled")]
    public async Task ColdMissHintsAreAsynchronousDeduplicatedAndDoNotHydrate(string mode)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;
        test.Dav.Put("cold.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);var path=Path.Combine(test.Local,"cold.bmp");
        var before=CloudFilesNative.InspectPersisted(path);var name="HybridMount.ThumbnailHints.Tests."+Guid.NewGuid().ToString("N");using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server=mode=="absent"?null:new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var received=0;var serve=Task.Run(async()=>
        {
            if(server is null)return;try
            {
                await server.WaitForConnectionAsync(stop.Token);if(mode=="wrongPeer"){var buffer=new byte[4];Assert.Equal(0,await server.ReadAsync(buffer,stop.Token));return;}
                var request=await IpcProtocol.ReadAsync<IpcRequest>(server,stop.Token);Interlocked.Increment(ref received);Assert.Equal(2,request.Version);Assert.Equal("requestThumbnail",request.Operation);Assert.Equal([path],request.Paths);
                if(mode=="stalled"){await Task.Delay(2000,stop.Token);return;}
                var state=test.Manager.RequestThumbnail(path,request.PreviewSize??64);await IpcProtocol.WriteAsync(server,new IpcResponse(2,request.RequestId,"ok","",PreviewStatus:state),stop.Token);
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        });
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"--diagnose-async-thumbnail",path,"64",mode=="ready"?"300":"100"})start.ArgumentList.Add(argument);
        start.Environment["HYBRIDMOUNT_TEST_THUMBNAIL_PIPE"]=name;start.Environment["HYBRIDMOUNT_TEST_THUMBNAIL_PEER"]=mode=="wrongPeer"?Path.Combine(test.Local,"wrong.exe"):Environment.ProcessPath;
        using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));Assert.Equal(0,process.ExitCode);Assert.Empty(await errors);await serve;
            using var result=JsonDocument.Parse(await output);var hints=result.RootElement.GetProperty("remote");Assert.Equal(1,hints.GetProperty("probes").GetInt32());Assert.Equal(1,hints.GetProperty("submitted").GetInt32());
            Assert.Equal(mode=="ready"?1:0,hints.GetProperty("sent").GetInt32());Assert.Equal(mode is "ready" or "stalled"?1:0,received);
            if(mode=="ready"){Assert.NotNull(test.Manager.GetPreview(path,64));Assert.Contains(result.RootElement.GetProperty("samples").EnumerateArray(),sample=>sample.GetProperty("hit").GetBoolean());}
            else Assert.All(result.RootElement.GetProperty("samples").EnumerateArray(),sample=>Assert.False(sample.GetProperty("hit").GetBoolean()));
            Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));var after=CloudFilesNative.InspectPersisted(path);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);
        }
        finally{await stop.CancelAsync();if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}await serve;}
    }
}
