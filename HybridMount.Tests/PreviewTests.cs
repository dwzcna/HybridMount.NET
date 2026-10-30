using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Xunit.Abstractions;

namespace HybridMount.Tests;

/// <summary>真实 NTFS／CFAPI、原生 COM 和独立 IPC 验收；不将直接调用处理器等同于 Explorer 自动选择入口。</summary>
[Collection("Native IPC")]
public sealed class PreviewTests(ITestOutputHelper output)
{
    /// <summary>后台预览和元数据扫描也可短暂持有句柄；只对明确的 oplock 竞争进行有界等待。</summary>
    private static async Task<CloudFilesNative.ProtectedHandle> OpenResourceReadAsync(string path)
    {
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(true)try{return CloudFilesNative.OpenRead(path);}
        catch(System.Runtime.InteropServices.COMException error)when(error.HResult==unchecked((int)0x80070322)&&DateTime.UtcNow<deadline){await Task.Delay(20);}
    }
    internal static byte[] LargeExecutable()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"Directory.Build.props")))directory=directory.Parent;
        Assert.NotNull(directory);
        var candidate=Path.Combine(directory.FullName,"HybridMount","bin","x64","Debug","Unpackaged","net10.0-windows10.0.26100.0","win-x64","HybridMount.NET.exe");
        if(!File.Exists(candidate))candidate=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
        var original=File.ReadAllBytes(candidate);var bytes=new byte[Math.Max(16*1024*1024,original.Length)];original.CopyTo(bytes,0);return bytes;
    }
    [Fact]
    public void ResourceExtractionUsesOnlyDataAndRejectsInvalidPe()
    {
        using var stream=new MemoryStream(LargeExecutable());var png=ExeIconExtractor.Extract(stream,64);
        Assert.NotNull(png);Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},png[..8]);
        Assert.Equal(64,BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));Assert.Equal(64,BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Null(ExeIconExtractor.Extract(new MemoryStream(new byte[100]),32));
    }
    [Fact(Timeout=120000)]
    public async Task ColdAndWarmCloudExePreviewsNeverHydrateAndLocalExtractionSurvivesDehydration()
    {
        await using var fixture=new DavFixture();fixture.Put("large.exe",LargeExecutable());
        var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);
        await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);
        var mount=await manager.AddAsync("EXE 预览测试",local,true,source:fixture.Options);
        try
        {
            var path=Path.Combine(local,"large.exe");var requests=new ConcurrentQueue<IpcRequest>();
            await using var server=new IpcServerService(logger,NativePreviewInterop.PipeName)
            {
                Handler=request=>
                {
                    requests.Enqueue(request);Assert.True(IpcProtocol.IsValid(request));
                    var png=manager.GetPreview(Assert.Single(request.Paths),request.PreviewSize!.Value);
                    return new(2,request.RequestId,png is null?"notFound":"ok","",PreviewPng:png);
                }
            };server.Start();
            Assert.Null(manager.GetPreview(path,64));Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="GET");
            output.WriteLine("before Shell item: GET="+fixture.Server.Requests.Count(request=>request.Method=="GET"));
            using(var provider=new NativePreviewInterop(path))
            {
                output.WriteLine("after Shell item: GET="+fixture.Server.Requests.Count(request=>request.Method=="GET"));
                var elapsed=Stopwatch.StartNew();Assert.Equal(0,provider.Thumbnail(64,out var alpha,out var bitmap));
                try{Assert.Equal(2u,alpha);Assert.NotEqual(0,bitmap);}finally{NativePreviewInterop.DeleteObject(bitmap);}
                output.WriteLine($"cold identity-only handler: {elapsed.ElapsedMilliseconds} ms; HTTP GET={fixture.Server.Requests.Count(request=>request.Method=="GET")}");
                Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="GET");
                Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
                Assert.NotEmpty(requests);Assert.All(requests,request=>Assert.Equal("getPreview",request.Operation));
                await manager.SetThumbnailsEnabledAsync(mount.Id,true);
                var keep=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"keepOnDevice",[path]));Assert.Equal("Completed",keep.State);
                var deadline=DateTime.UtcNow.AddSeconds(15);byte[]? png=null;
                while(DateTime.UtcNow<deadline&&(png=manager.GetPreview(path,128)) is null)await Task.Delay(25);
                Assert.NotNull(png);Assert.Equal(128,BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
                var gets=fixture.Server.Requests.Count(request=>request.Method=="GET");
                Assert.Equal(0,provider.Thumbnail(80,out alpha,out bitmap));try{Assert.Equal(2u,alpha);Assert.Equal(128,NativePreviewInterop.BitmapWidth(bitmap));}finally{NativePreviewInterop.DeleteObject(bitmap);}
                Assert.Equal(gets,fixture.Server.Requests.Count(request=>request.Method=="GET"));
                output.WriteLine("warm cache ready; dehydrate begins");
                var free=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"freeSpace",[path]));Assert.Equal("Completed",free.State);
                Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.NotNull(manager.GetPreview(path,64));
                Assert.Equal(0,provider.Thumbnail(128,out alpha,out bitmap));NativePreviewInterop.DeleteObject(bitmap);
                Assert.Equal(gets,fixture.Server.Requests.Count(request=>request.Method=="GET"));
                // 普通 File.Copy 继续按需读取，目标为挂载外的隔离临时文件，不执行 EXE。
                output.WriteLine("ordinary copy begins");
                var copy=Path.Combine(fixture.Area,"copied.exe");await Task.Run(()=>File.Copy(path,copy)).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(fixture.Objects["large.exe"].Bytes,await File.ReadAllBytesAsync(copy));
                output.WriteLine("ordinary copy completed; read-only resource begins");
                var attributes=File.GetAttributes(path);File.SetAttributes(path,attributes|FileAttributes.ReadOnly);
                try
                {
                    using var readHandle=await OpenResourceReadAsync(path);Assert.True(CloudFilesNative.CfReferenceProtectedHandle(readHandle));
                    try{using var content=new ProtectedResourceStream(CloudFilesNative.CfGetWin32HandleFromProtectedHandle(readHandle),new FileInfo(path).Length);Assert.NotNull(ExeIconExtractor.Extract(content,32));}
                    finally{CloudFilesNative.CfReleaseProtectedHandle(readHandle);}
                }
                finally{File.SetAttributes(path,attributes);}
                await WebDavTests.Finished(manager,manager.Queue(mount.Id,"freeSpace",[path]));
                var oldEntry=repository.Entries(mount.Id).Single(entry=>entry.LocalPath=="large.exe");
                fixture.Put("large.exe",LargeExecutable());var refresh=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"refresh"));Assert.Equal("Completed",refresh.State);
                var newEntry=repository.Entries(mount.Id).Single(entry=>entry.LocalPath=="large.exe");Assert.NotEqual(oldEntry.Version,newEntry.Version);
                Assert.NotEqual(ExePreviewCache.CacheKey(mount,oldEntry,64),ExePreviewCache.CacheKey(mount,newEntry,64));Assert.Null(manager.GetPreview(path,64));
                manager.CacheLimitBytes=1;await manager.EnforceCacheLimitAsync();Assert.Empty(Directory.EnumerateFiles(Path.Combine(repository.DataDirectory,"PreviewCache"),"*.cache"));
            }
            using(var other=new NativePreviewInterop(Path.Combine(local,"文件.bin")))
            {var count=requests.Count;Assert.Equal(unchecked((int)0x80004001),other.Thumbnail(64,out _,out var bitmap));Assert.Equal(0,bitmap);Assert.Equal(count,requests.Count);}
        }
        finally
        {
            foreach(var message in (await logger.ReadAsync()))output.WriteLine(message.Message);
            var clear=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Assert.Equal("Completed",clear.State);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
    [Fact(Timeout=120000)]
    public async Task ActualShellBindingIsMeasuredSeparatelyFromIdentityOnlyHandler()
    {
        await using var fixture=new DavFixture{IgnoreRange=true};fixture.Put("shell-probe.exe",LargeExecutable());
        var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);var mount=await manager.AddAsync("Shell 绑定测量",local,true,source:fixture.Options);
        try
        {
            var path=Path.Combine(local,"shell-probe.exe");
            var before=fixture.Server.Requests.Count(request=>request.Method=="GET");
            using var native=new NativePreviewInterop(path,true,stage=>output.WriteLine($"{stage}: GET={fixture.Server.Requests.Count(request=>request.Method=="GET")}; full={CloudFilesNative.HasFullContent(path,new FileInfo(path).Length)}"));
            var afterBinding=fixture.Server.Requests.Count(request=>request.Method=="GET");
            Assert.Equal(0,native.Thumbnail(32,out _,out var bitmap));NativePreviewInterop.DeleteObject(bitmap);
            var afterHandler=fixture.Server.Requests.Count(request=>request.Method=="GET");
            output.WriteLine($"Shell binding delta={afterBinding-before}; handler delta={afterHandler-afterBinding}; cloud thumbnail production registration remains disabled pending Explorer acceptance");
            Assert.Equal(afterBinding,afterHandler);
        }
        finally
        {
            var result=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Assert.Equal("Completed",result.State);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }

    [Theory(Timeout=120000)]
    [InlineData(16,4u)][InlineData(256,4u)][InlineData(256,0u)]
    public async Task RealShellImageFactoryReportsAutomaticExeReads(int size,uint flags)
    {
        await using var fixture=new DavFixture();fixture.Put("automatic-"+Guid.NewGuid().ToString("N")+".exe",LargeExecutable());
        var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);var mount=await manager.AddAsync("Shell 自动入口测量",local,true,source:fixture.Options);
        try
        {
            var path=Path.Combine(local,fixture.Objects.Keys.Single(key=>key.EndsWith(".exe",StringComparison.Ordinal)));
            var watch=Stopwatch.StartNew();var status=await Task.Run(()=>
            {
                var code=NativePreviewInterop.ShellImage(path,size,flags,out var bitmap);if(bitmap!=0)NativePreviewInterop.DeleteObject(bitmap);return code;
            }).WaitAsync(TimeSpan.FromSeconds(20));
            var gets=fixture.Server.Requests.Where(request=>request.Method=="GET").ToArray();
            var bytes=gets.Sum(request=>request.Headers.TryGetValue("Range",out var range)?long.Parse(range[6..].Split('-')[1])-long.Parse(range[6..].Split('-')[0])+1:fixture.Objects.Values.Max(item=>item.Bytes.Length));
            output.WriteLine($"IShellItemImageFactory size={size}, flags={flags}, HRESULT=0x{status:X8}, elapsedMs={watch.ElapsedMilliseconds}, GET={gets.Length}, requestedBytes={bytes}; default cloud thumbnail registration=false");
            Assert.True(status>=0,$"Shell image factory failed: 0x{status:X8}");
        }
        finally
        {
            var result=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Assert.Equal("Completed",result.State);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }

    [Theory(Timeout=30000)]
    [InlineData("old")][InlineData("wrongId")][InlineData("badPng")][InlineData("timeout")]
    public async Task UnavailablePreviewDataFallsBackWithoutHydration(string failure)
    {
        await using var fixture=new DavFixture();fixture.Put("fallback.exe",LargeExecutable());
        var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);var mount=await manager.AddAsync("预览回退测试",local,true,source:fixture.Options);
        try
        {
            await using var server=new IpcServerService(logger,NativePreviewInterop.PipeName)
            {
                Handler=request=>
                {
                    if(failure=="timeout")Thread.Sleep(400);
                    return new(failure=="old"?1:2,failure=="wrongId"?Guid.NewGuid().ToString():request.RequestId,failure=="old"?"notImplemented":"ok","",PreviewPng:[1,2,3]);
                }
            };server.Start();
            using var provider=new NativePreviewInterop(Path.Combine(local,"fallback.exe"));var watch=Stopwatch.StartNew();
            Assert.Equal(0,provider.Thumbnail(80,out var alpha,out var bitmap));
            try{Assert.Equal(2u,alpha);Assert.Equal(80,NativePreviewInterop.BitmapWidth(bitmap));}finally{NativePreviewInterop.DeleteObject(bitmap);}
            output.WriteLine($"fallback={failure}; elapsedMs={watch.ElapsedMilliseconds}; HTTP GET=0");
            Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="GET");
            Assert.False(CloudFilesNative.HasFullContent(Path.Combine(local,"fallback.exe"),new FileInfo(Path.Combine(local,"fallback.exe")).Length));
        }
        finally
        {
            var result=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Assert.Equal("Completed",result.State);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }

    [Theory]
    [InlineData(0,1)][InlineData(64,0)][InlineData(64,2)][InlineData(1024,1)]
    public void PreviewContractRequiresOnePathAndBoundedSize(int size,int count)
    {
        var request=new IpcRequest(2,Guid.NewGuid().ToString(),"getPreview",Enumerable.Repeat(@"C:\view\a.exe",count).ToArray(),PreviewSize:size);
        Assert.False(IpcProtocol.IsValid(request));
    }
    [Fact]
    public void PreviewContractAcceptsCompatibleOptionalPngWithoutChangingV2()
    {
        Assert.Equal(2,IpcProtocol.Version);Assert.True(IpcProtocol.IsValid(new(2,Guid.NewGuid().ToString(),"getPreview",[@"C:\view\a.exe"],PreviewSize:64)));
    }
}
