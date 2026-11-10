using System.Diagnostics;
using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Tests;

/// <summary>实际原生进程1000次缓存查询；保持进程存活验证空闲连接不妨碍C#唯一写入者删除库。</summary>
[Collection("Native IPC")]
public sealed class NativeThumbnailReadReuseTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact(Timeout=90000)]
    public async Task ReusedReadOnlyStatementsEndEachSnapshotAndReleaseIdleDatabaseHandle()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("pooled.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var entry=test.Repository.Entries(test.Id).Single(item=>item.LocalPath=="pooled.bmp");var mount=test.Manager.Mounts.Single().Definition;var path=Path.Combine(test.Local,entry.LocalPath);
        var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(mount with{ThumbnailsEnabled=true}),true);
        var image=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png"));Assert.True(store.Publish(ThumbnailCacheReader.Capture(root,entry)!,new Dictionary<int,byte[]>{{64,image}}));
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--test-thumbnail-reader");
        using var process=Process.Start(start)!;var errors=process.StandardError.ReadToEndAsync();var before=test.Dav.Server.Requests.Count;
        try
        {
            await IpcProtocol.WriteAsync(process.StandardInput.BaseStream,new{path,samples=1000});using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response=await IpcProtocol.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,deadline.Token);output.WriteLine(response.GetRawText());
            Assert.Equal(1000,response.GetProperty("samples").GetArrayLength());Assert.All(response.GetProperty("samples").EnumerateArray(),sample=>Assert.True(sample.GetProperty("hit").GetBoolean()));
            Assert.InRange(response.GetProperty("query").GetProperty("opened").GetInt64(),1,2);Assert.True(response.GetProperty("query").GetProperty("reused").GetInt64()>=998);
            Assert.False(process.HasExited);store.DeleteDatabase(root);Assert.False(File.Exists(ThumbnailCachePaths.ForMount(test.Id)));Assert.Equal(before,test.Dav.Server.Requests.Count);
            Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
            await IpcProtocol.WriteAsync(process.StandardInput.BaseStream,new{kind="stop"});await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(0,process.ExitCode);Assert.Empty(await errors);
        }
        finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}store.DeleteDatabase(store.Root(test.Id));}
    }
}
