using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Services.Mounting;
using Microsoft.Win32;
namespace HybridMount.Tests;

/// <summary>由独立临时MSIX身份显式运行的Shell自动选择验收；不是普通图标成功断言，不计入未执行的单元测试。</summary>
public static class ShellIntegrationProbe
{
    /// <summary>使用真实图像工厂、隔离CLSID和跨进程管道核对可识别像素，finally按同包身份清理测试根。</summary>
    public static async Task<int> RunAsync(string output,string tag,string family)
    {
        var proof=new Dictionary<string,object?>();var calls=new ConcurrentQueue<string>();UploadFlowTests.Fixture? test=null;var success=false;
        try
        {
            var package=global::Windows.ApplicationModel.Package.Current;Assert.Equal(family,package.Id.FamilyName);Assert.StartsWith("HybridMount.ShellProbe.",package.Id.Name);
            proof["Package"]=package.Id.FullName;proof["PFN"]=family;proof["Tag"]=tag;
            test=CreateProbe(output,"content");await ConnectProbeAsync(test);var mount=test.Manager.Mounts.Single().Definition;proof["Root"]=mount;proof["SyncRootId"]=CloudRootRegistration.Id(mount);
            using(var key=Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\"+CloudRootRegistration.Id(mount)))
            {Assert.NotNull(key);proof["AUMID"]=key.GetValue("AUMID");proof["ThumbnailProvider"]=key.GetValue("ThumbnailProvider");Assert.Equal(family+"!App",key.GetValue("AUMID"));}
            proof["RegistrationMatrix"]=await MatrixAsync(output,tag,test);
            var warm="warm-"+Guid.NewGuid().ToString("N")+".bmp";var cold="cold-"+Guid.NewGuid().ToString("N")+".bmp";
            test.Dav.Put(warm,PreviewPipelineTests.Bmp());test.Dav.Put(cold,PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
            var warmPath=Path.Combine(test.Local,warm);var coldPath=Path.Combine(test.Local,cold);await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);
            await using var server=new IpcServerService(test.Logger,IpcProtocol.PipeName+".shell-probe."+tag){Handler=request=>
            {
                calls.Enqueue(request.Operation+":"+Path.GetFileName(request.Paths[0]));
                if(request.Operation=="requestPreview")return new(2,request.RequestId,"accepted","",PreviewStatus:test.Manager.RequestPreview(request.Paths[0],request.PreviewSize!.Value));
                var png=test.Manager.GetPreview(request.Paths[0],request.PreviewSize!.Value);return new(2,request.RequestId,png is null?"notFound":"ok","",PreviewPng:png,PreviewStatus:test.Manager.PreviewState(request.Paths[0]));
            }};server.Start();
            test.Manager.RequestPreview(warmPath,64);await Ready(test.Manager,warmPath);
            var warmImage=await Image(warmPath);proof["Warm"]=warmImage;proof["WarmCalls"]=calls.ToArray();Assert.True(warmImage.Pattern,"Shell did not automatically choose the registered thumbnail handler.");Assert.NotEmpty(calls);
            var first=await Image(coldPath);proof["ColdFirst"]=first;await Ready(test.Manager,coldPath);await Task.Delay(300);
            var next=await Image(coldPath);proof["ColdAfterNotify"]=next;proof["ColdRefreshWorks"]=next.Pattern;
            proof["ShellState"]=CloudShellState.Read(coldPath);proof["Calls"]=calls.ToArray();proof["Requests"]=test.Dav.Server.Requests.Select(r=>new{r.Method,r.Target,Range=r.Headers.GetValueOrDefault("Range")}).ToArray();
            proof["WarmHydrated"]=CloudFilesNative.HasFullContent(warmPath,new FileInfo(warmPath).Length);proof["ColdHydrated"]=CloudFilesNative.HasFullContent(coldPath,new FileInfo(coldPath).Length);
            Assert.False((bool)proof["WarmHydrated"]!);Assert.False((bool)proof["ColdHydrated"]!);Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method is "PUT" or "MKCOL");
            Assert.True(next.Pattern,"Initial generic icon remained cached after UPDATEITEM.");success=true;
        }
        catch(Exception error){proof["Error"]=error.ToString();}
        finally
        {
            try{if(test is not null)await FinishProbeAsync(output,test);VerifyCleanup(output);proof["Cleanup"]=true;}
            catch(Exception error){proof["Cleanup"]=false;proof["CleanupError"]=error.ToString();success=false;}
            using(var trace=Registry.CurrentUser.OpenSubKey(@"Software\HybridMount.NET.Tests\ShellProbe\"+tag))proof["NativeTrace"]=trace?.GetValueNames().ToDictionary(x=>x,x=>trace.GetValue(x));
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\HybridMount.NET.Tests\ShellProbe\"+tag,false);
            proof["Success"]=success;Directory.CreateDirectory(Path.GetDirectoryName(output)!);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(proof,new JsonSerializerOptions{WriteIndented=true}));
        }
        return success?0:1;
    }
    private static async Task<object[]> MatrixAsync(string output,string tag,UploadFlowTests.Fixture fixture)
    {
        var results=new List<object>();var basePath=Path.Combine(Path.GetDirectoryName(output)!,"matrix");Directory.CreateDirectory(basePath);
        var bytes=PreviewPipelineTests.Bmp();var budget=new PreviewRangeBudget(bytes.Length,(offset,buffer,_)=>{bytes.AsMemory((int)offset,buffer.Length).CopyTo(buffer);return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));});
        var frames=await new PreviewWorkerClient().DecodeAsync(".bmp",bytes.Length,budget.ReadAsync);Assert.NotNull(frames);
        var count=0;await using var logger=new LoggerService(Path.Combine(basePath,"log.db"));
        await using var server=new IpcServerService(logger,IpcProtocol.PipeName+".shell-probe."+tag){Handler=request=>{Interlocked.Increment(ref count);return new(2,request.RequestId,"ok","",PreviewPng:frames[64],PreviewStatus:"ready");}};server.Start();
        foreach(var policy in new[]{global::Windows.Storage.Provider.StorageProviderHydrationPolicy.Partial,global::Windows.Storage.Provider.StorageProviderHydrationPolicy.Progressive,global::Windows.Storage.Provider.StorageProviderHydrationPolicy.Full})
        {
            var mount=fixture.Manager.Mounts.Single().Definition;fixture.Dav.Put("policy.bmp",bytes);await WebDavTests.Finished(fixture.Manager,fixture.Manager.Queue(fixture.Id,"refresh"));
            await fixture.Manager.DisconnectAsync(fixture.Id);
            CloudRootPolicy.Set(mount,(ushort)policy,default);await fixture.Manager.ConnectAsync(fixture.Id);
            var before=count;var image=await Image(Path.Combine(fixture.Local,"policy.bmp"));using var key=Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\"+CloudRootRegistration.Id(mount));
            results.Add(new{Policy=policy.ToString(),NativePolicy=CloudRootPolicy.Read(fixture.Local),AUMID=key?.GetValue("AUMID"),Provider=key?.GetValue("ThumbnailProvider"),Calls=count-before,Image=image,State=CloudShellState.Read(Path.Combine(fixture.Local,"policy.bmp")),Hydrated=CloudFilesNative.HasFullContent(Path.Combine(fixture.Local,"policy.bmp"),bytes.Length)});
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(output)!,"matrix.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        }
        return results.ToArray();
    }
    /// <summary>测试根置于本轮项目产物下，避免MSIX的AppData虚拟化让系统注册看到另一目录；实例先返回再连接，失败也能收尾。</summary>
    private static UploadFlowTests.Fixture CreateProbe(string output,string name)
    {
        var test=new UploadFlowTests.Fixture();test.Local=Path.Combine(Path.GetDirectoryName(output)!,"roots",name+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(test.Local);
        test.Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        test.Repository=new(Path.Combine(test.Dav.Area,"data"),test.Registry);test.Logger=new(Path.Combine(test.Dav.Area,"logs.db"));test.Manager=new(test.Repository,test.Logger);
        return test;
    }
    /// <summary>连接前已保留夹具，失败时仍从内存定义取得精确ID，不把未赋值实例误记为已清理。</summary>
    private static async Task ConnectProbeAsync(UploadFlowTests.Fixture test)
    {
        try{await test.Manager.AddAsync("Shell隔离验证",test.Local,true,source:test.Dav.Options,credentials:new("user","app-password"),autoUpload:false);}
        finally{test.Id=test.Manager.Mounts.Single().Definition.Id;}
        await test.Manager.SetSchedulingForTestsAsync(test.Id,false);
    }
    /// <summary>先断开本轮独立后台，再复用遗留恢复的句柄保护及原生注销回退；包卸载前核验注册、身份和目录收尾。</summary>
    private static async Task FinishProbeAsync(string output,UploadFlowTests.Fixture test)
    {
        await test.Manager.DisposeAsync();
        var recovery=new OrphanedMountRecovery(Path.Combine(Path.GetDirectoryName(output)!,"recovery"),()=>[],test.Logger);
        if(CloudFilesNative.RootIdentity(test.Local,true) is not null)
        {
            var result=await recovery.ClearAsync(recovery.Inspect(test.Local),default);
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(output)!,"cleanup.json"),JsonSerializer.Serialize(result));Assert.True(result.Completed,string.Join(";",result.Errors));
        }
        await test.Logger.DisposeAsync();Registry.CurrentUser.DeleteSubKeyTree(test.Registry,false);await test.Dav.DisposeAsync();
    }
    /// <summary>核对本轮仍存在目录没有原生根或Shell引用；只读检查，不因Dispose未抛就宣称完整清理。</summary>
    private static void VerifyCleanup(string output)
    {
        var roots=Path.Combine(Path.GetDirectoryName(output)!,"roots");
        foreach(var path in Directory.Exists(roots)?Directory.GetDirectories(roots):[])
        {
            Assert.Null(CloudFilesNative.RootIdentity(path,true));
            using var manager=Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if(manager is null)continue;
            foreach(var name in manager.GetSubKeyNames())
            {
                using var key=manager.OpenSubKey(name);using var users=key?.OpenSubKey("UserSyncRoots");
                foreach(var user in users?.GetValueNames()??[])Assert.NotEqual(path,users!.GetValue(user) as string,StringComparer.OrdinalIgnoreCase);
            }
        }
    }
    private static async Task Ready(MountManager manager,string path)
    {var until=DateTime.UtcNow.AddSeconds(20);while(manager.GetPreview(path,64) is null&&DateTime.UtcNow<until)await Task.Delay(40);Assert.NotNull(manager.GetPreview(path,64));}
    private sealed record ImageResult(int HResult,int Width,int Height,int PatternPixels,bool Pattern);
    private static Task<ImageResult> Image(string path)=>Task.Run(()=>
    {
        var status=NativeThumbnailInterop.ShellImage(path,64,8,out var bitmap);if(status<0||bitmap==0)return new ImageResult(status,0,0,0,false);
        try
        {
            var metadata=new byte[32];Assert.Equal(32,GetObject(bitmap,32,metadata));var width=BitConverter.ToInt32(metadata,4);var height=BitConverter.ToInt32(metadata,8);
            Assert.InRange(width,1,256);Assert.InRange(height,1,256);var pixels=new byte[width*height*4];var info=new BitmapInfo{Size=40,Width=width,Height=-height,Planes=1,Bits=32};var dc=CreateCompatibleDC(0);
            try{Assert.Equal(height,GetDIBits(dc,bitmap,0,(uint)height,pixels,ref info,0));}finally{DeleteDC(dc);}
            var count=0;for(var offset=0;offset<pixels.Length;offset+=4)if(pixels[offset+2] is >=167 and <=173&&pixels[offset+3]>240)count++;
            return new ImageResult(status,width,height,count,count>width*height*0.8);
        }
        finally{NativeThumbnailInterop.DeleteObject(bitmap);}
    });
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public uint Size;public int Width,Height;public ushort Planes,Bits;public uint Compression,ImageSize;public int X,Y;public uint Used,Important;}
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")]private static extern int GetObject(nint bitmap,int length,byte[] data);
    [DllImport("gdi32.dll")]private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]private static extern int GetDIBits(nint dc,nint bitmap,uint start,uint lines,byte[] pixels,ref BitmapInfo info,uint usage);
}
