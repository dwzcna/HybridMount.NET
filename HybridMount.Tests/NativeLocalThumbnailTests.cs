using System.Diagnostics;
using System.Text.Json;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>无缓存本地解码与冷文件保护；真实原生工作进程不加载主程序，不写缩略图库或更改云状态。</summary>
[Collection("Native IPC")]
public sealed class NativeLocalThumbnailTests
{
    [Fact(Timeout=90000)]
    public async Task FullPlaceholderAfterDisconnectDecodesWithoutCacheBindingAndHonorsDisabledRoot()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var bytes=PreviewPipelineTests.Bmp();test.Dav.Put("full.bmp",bytes);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"full.bmp");
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);
        var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try {
            await test.Manager.DisconnectAsync(test.Id);var before=CloudFilesNative.InspectPersisted(path);var stamp=LocalFileState.Read(path);var requests=test.Dav.Server.Requests.Count;
            var result=await Read(path);Assert.True(result.GetProperty("hit").GetBoolean(),result.ToString());Assert.Equal("local-ready",result.GetProperty("stage").GetString());
            var after=CloudFilesNative.InspectPersisted(path);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);Assert.Equal(stamp.Identity,LocalFileState.Read(path).Identity);Assert.Equal(bytes,File.ReadAllBytes(path));Assert.Equal(requests,test.Dav.Server.Requests.Count);
            Assert.Null(store.Binding(path));store.Configure(root with{Enabled=false});Assert.False((await Read(path)).GetProperty("hit").GetBoolean());
        }finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task OrdinaryFileCanDecodeWithoutBecomingPlaceholderOrWritingCache()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"ordinary.png");var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","sample.png"));await File.WriteAllBytesAsync(path,bytes);
        var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try{await test.Manager.DisconnectAsync(test.Id);var stamp=LocalFileState.Read(path);var requests=test.Dav.Server.Requests.Count;Assert.True((await Read(path)).GetProperty("hit").GetBoolean());Assert.Null(CloudFilesNative.Inspect(path));Assert.Equal(stamp.Identity,LocalFileState.Read(path).Identity);Assert.Equal(bytes,File.ReadAllBytes(path));Assert.Null(store.Binding(path));Assert.Equal(requests,test.Dav.Server.Requests.Count);}
        finally{store.DeleteDatabase(root);}
    }
    [Fact(Timeout=90000)]
    public async Task ColdMissNeverReadsSourceOrHydrates()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("cold.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"cold.bmp");
        var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try{var requests=test.Dav.Server.Requests.Count;var result=await Read(path);Assert.False(result.GetProperty("hit").GetBoolean());Assert.Equal("local-completeness",result.GetProperty("stage").GetString());Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Null(store.Binding(path));}
        finally{store.DeleteDatabase(root);}
    }
    [Fact(Timeout=60000)]
    public async Task LocalBudgetAcceptsMediaLargerThanRemoteBudget()
    {
        var bytes=PreviewPipelineTests.Bmp(2048,2048);ValueTask<PreviewRangeResult> Supply(long offset,Memory<byte> buffer,CancellationToken token){token.ThrowIfCancellationRequested();bytes.AsMemory((int)offset,buffer.Length).CopyTo(buffer);return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));}
        var local=PreviewRangeBudget.ForLocal(bytes.Length,Supply);var frames=await new PreviewWorkerClient().DecodeAsync(".bmp",bytes.Length,local.ReadAsync);Assert.NotNull(frames);Assert.True(local.Bytes>PreviewRangeBudget.MaximumBytes);
        var remote=new PreviewRangeBudget(bytes.Length,Supply);await Assert.ThrowsAsync<IOException>(()=>new PreviewWorkerClient().DecodeAsync(".bmp",bytes.Length,remote.ReadAsync));
    }
    [Fact(Timeout=60000)]
    public async Task LargeImageAboveOldPixelLimitDecodesWithinLocalBudget()
    {
        var bytes=LargePng();ValueTask<PreviewRangeResult> Supply(long offset,Memory<byte> buffer,CancellationToken token){token.ThrowIfCancellationRequested();bytes.AsMemory((int)offset,buffer.Length).CopyTo(buffer);return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));}
        var budget=PreviewRangeBudget.ForLocal(bytes.Length,Supply);var frames=await new PreviewWorkerClient().DecodeAsync(".bmp",bytes.Length,budget.ReadAsync);Assert.NotNull(frames);Assert.All(frames.Values,png=>Assert.InRange(png.Length,33,ExePreviewCache.MaximumPngBytes));Assert.InRange(budget.Bytes,1,128*1024*1024);Assert.InRange(budget.Requests,1,512);
    }
    /// <summary>内存生成3355万像素灰度PNG，独立验证像素门限；大字节读取预算由上一项BMP回归验证。</summary>
    private static byte[] LargePng()
    {
        using var image=new MemoryStream();image.Write(new byte[]{137,80,78,71,13,10,26,10});
        void Chunk(string name,byte[] data){var type=System.Text.Encoding.ASCII.GetBytes(name);Span<byte> word=stackalloc byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(word,data.Length);image.Write(word);image.Write(type);image.Write(data);uint crc=uint.MaxValue;foreach(var value in type.Concat(data)){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xEDB88320u:0);}System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(word,~crc);image.Write(word);}
        var header=new byte[13];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header,8192);System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),4096);header[8]=8;Chunk("IHDR",header);
        using var compressed=new MemoryStream();using(var deflate=new System.IO.Compression.ZLibStream(compressed,System.IO.Compression.CompressionLevel.Fastest,true)){var row=new byte[8193];row.AsSpan(1).Fill(170);for(var y=0;y<4096;y++)deflate.Write(row);}
        Chunk("IDAT",compressed.ToArray());Chunk("IEND",[]);return image.ToArray();
    }
    internal static async Task<JsonElement> Read(string path)
    {
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"--diagnose-available-thumbnail",path,"64"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));Assert.Equal(0,process.ExitCode);Assert.Empty(await error);using var doc=JsonDocument.Parse(await output);return doc.RootElement.Clone();
    }
}
