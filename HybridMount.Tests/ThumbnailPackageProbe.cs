using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Services.Mounting;
using Microsoft.Win32;
namespace HybridMount.Tests;

/// <summary>显式运行的真实MSIX缩略图门槛；仅使用GUID隔离CFAPI根、回环服务和可精确回收的共享缓存行。</summary>
public static class ThumbnailPackageProbe
{
    /// <summary>测试relay的早期角色使用生产宿主／维护协议，正常模式不创建WinUI或正式配置。</summary>
    public static Task<int> MainAsync(string[] args,string area)
    {
        if(ThumbnailServer.IsEntry(args))
        {
            var started=new{Package=Windows.ApplicationModel.Package.Current.Id.FullName,Directory=ThumbnailCachePaths.DirectoryPath,Pid=Environment.ProcessId};
            File.WriteAllText(Path.Combine(area,"thumbnail-host-"+Environment.ProcessId+".json"),JsonSerializer.Serialize(started));
            return Task.FromResult(ThumbnailServer.Run(args));
        }
        if(args.Length>0&&args[0]=="--shell-maintenance")return Task.FromResult(ShellMaintenanceProcess.Run(args));
        if(args is ["--read",var path])
        {
            if(WinRegisterService.IsPackaged())return Task.FromResult(2);
            var directory=Path.GetDirectoryName(path)!;var names=new[]{"cloud.bmp","cached.bmp","pinned.bmp","released.bmp"};var images=new Dictionary<string,ImageResult>();
            var folder=NativeShellInspector.Read(directory,directory,names);
            if(!File.Exists(Path.Combine(area,"empty-profile")))foreach(var name in names)images[name]=Image(Path.Combine(directory,name));
            var direct=File.Exists(Path.Combine(area,"clsid.txt"))&&!File.Exists(Path.Combine(area,"empty-profile"))?DirectImage(path,Guid.Parse(File.ReadAllText(Path.Combine(area,"clsid.txt")))):null;
            var database=ThumbnailCachePaths.ForMount(MountPathGuard.ReadRoot(path)!.MountId);var value=new{Direct=direct,Database=database,FileId=MountPathGuard.FileIdentity(database),Cached=new ThumbnailCacheReader(ThumbnailCachePaths.DirectoryPath,true).TryGet(path,64) is not null,Folder=folder,Images=images};
            Save(area,"outside-cache.json",value).GetAwaiter().GetResult();return Task.FromResult(value.Cached?0:1);
        }
        return RunAsync(area);
    }
    private static async Task<int> RunAsync(string area)
    {
        var report=new Dictionary<string,object?>();UploadFlowTests.Fixture? test=null;ThumbnailCacheRoot? cacheRoot=null;ThumbnailCacheStore? store=null;var success=false;
        try
        {
            var empty=File.Exists(Path.Combine(area,"empty-profile"));report["EmptyCloudFiles"]=empty;var package=Windows.ApplicationModel.Package.Current;Assert.StartsWith("HybridMount.ThumbnailProbe.",package.Id.Name);report["Package"]=package.Id.FullName;
            test=new();test.Local=Path.Combine(area,"root-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(test.Local);test.Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
            test.Repository=new(Path.Combine(area,"data"),test.Registry);test.Logger=new(Path.Combine(area,"log.db"));test.Manager=new(test.Repository,test.Logger){RemovalShellForTests=new MaintenanceShellRegistration()};
            var names=new[]{"cloud.bmp","cached.bmp","pinned.bmp","released.bmp"};var actual=PreviewPipelineTests.Bmp();for(var offset=54;offset<actual.Length;offset+=3)actual[offset+2]=30;
            foreach(var name in names)test.Dav.Put(name,actual);
            MountDefinition mount;
            try{mount=await test.Manager.AddAsync("MySvr-00939-"+Guid.NewGuid().ToString("N"),test.Local,false,source:test.Dav.Options,credentials:new("user","app-password"),autoUpload:false);}
            catch(COMException error)when(error.HResult==unchecked((int)0x80070490)&&test.Manager.Mounts.Count==1)
            {
                test.Id=test.Manager.Mounts.Single().Definition.Id;report["FirstRegistrationHResult"]=error.HResult;await Task.Delay(3000);await test.Manager.ConnectAsync(test.Id);mount=test.Manager.Mounts.Single().Definition;
            }
            test.Id=mount.Id;store=new(ThumbnailCachePaths.ForMount(mount.Id));await test.Manager.SetSchedulingForTestsAsync(test.Id,false);
            report["Root"]=mount;report["Registry"]=test.Registry;report["RootId"]=CloudRootRegistration.Id(mount);await Save(area,"registered.json",report);
            foreach(var name in names.Where(x=>x!="cloud.bmp"))Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[Path.Combine(test.Local,name)]))).State);
            Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[Path.Combine(test.Local,"released.bmp")]))).State);
            using(var handle=CloudFilesNative.CreateFile(Path.Combine(test.Local,"cached.bmp"),0x180,7,0,3,0x02200000,0))CloudFilesNative.Check(CloudFilesNative.SetPinIntent(handle,0,0,0));
            // 合成缓存像素与真实正文不同；Shell自动返回该像素才能证明云处理器被选择。
            var pattern=PreviewPipelineTests.Bmp();var budget=new PreviewRangeBudget(pattern.Length,(offset,buffer,_)=>{pattern.AsMemory((int)offset,buffer.Length).CopyTo(buffer);return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));});
            var frames=await new PreviewWorkerClient().DecodeAsync(".bmp",pattern.Length,budget.ReadAsync);Assert.NotNull(frames);
            cacheRoot=store.Configure(ThumbnailCacheReader.RootFor(mount with{ThumbnailsEnabled=true}));report["CacheRoot"]=cacheRoot;
            foreach(var entry in test.Repository.Entries(test.Id).Where(x=>!x.IsDirectory&&PreviewWorker.Supports(x.LocalPath))){var binding=ThumbnailCacheReader.Capture(cacheRoot,entry);Assert.NotNull(binding);Assert.True(store.Publish(binding,frames));}
            var before=Facts(test,names);var count=test.Dav.Server.Requests.Count;report["FilesBefore"]=before;report["Database"]=store.Database;report["DatabaseFileId"]=MountPathGuard.FileIdentity(store.Database);report["CacheReadPath"]=Path.Combine(test.Local,"cloud.bmp");await Save(area,"cache-ready.json",report);
            // 独立非打包进程在读取门槛期间核实共享物理FileId，避免只证明包内虚拟副本。
            var deadline=DateTime.UtcNow.AddSeconds(90);while(!File.Exists(Path.Combine(area,"outside-cache.json"))&&DateTime.UtcNow<deadline)await Task.Delay(100);
            Assert.True(File.Exists(Path.Combine(area,"outside-cache.json")),"包外缓存读取没有完成。");using(var outside=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(area,"outside-cache.json")))){Assert.Equal((string)report["DatabaseFileId"]!,outside.RootElement.GetProperty("FileId").GetString());Assert.True(outside.RootElement.GetProperty("Cached").GetBoolean());}
            report["Outside"]=JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(area,"outside-cache.json")));
            report["Physical"]=NativeShellInspector.Read(test.Local,test.Local,names);string? ns=null;
            using(var key=Registry.LocalMachine.OpenSubKey(RecoveryShellRegistration.Manager+"\\"+CloudRootRegistration.Id(mount))){report["AUMID"]=key?.GetValue("AUMID");ns=key?.GetValue("NamespaceCLSID") as string;}
            if(ns is not null)report["Namespace"]=NativeShellInspector.Read("::"+ns,test.Local,names);
            var physical=(NativeShellInspector.FolderFacts)report["Physical"]!;Assert.Equal(0,physical.BindResult);Assert.Contains(physical.Columns,x=>x.Name=="System.StorageProviderUIStatus");var nativeMenus=physical.Files.Any(x=>x.Menu.Any(s=>s.Contains("始终保留",StringComparison.Ordinal)))&&physical.Files.Any(x=>x.Menu.Any(s=>s.Contains("释放空间",StringComparison.Ordinal)));report["NativeMenusAvailable"]=nativeMenus;
            var images=new Dictionary<string,ImageResult>();if(!empty)foreach(var name in names){var image=Image(Path.Combine(test.Local,name));images[name]=image;}report["Images"]=images;report["ShellProperties"]=names.Select(name=>new{Name=name,Properties=CloudShellState.Read(Path.Combine(test.Local,name))}).ToArray();
            report["FilesAfter"]=Facts(test,names);Assert.Equal(JsonSerializer.Serialize(Stable(before)),JsonSerializer.Serialize(Stable(report["FilesAfter"]!)));Assert.Equal(count,test.Dav.Server.Requests.Count);report["RequestsBefore"]=count;report["RequestsAfter"]=test.Dav.Server.Requests.Count;var outsideResult=(JsonElement)report["Outside"]!;var outsideFolder=outsideResult.GetProperty("Folder").Deserialize<NativeShellInspector.FolderFacts>()!;
            var outsideMenus=outsideFolder.Files.Any(x=>x.Menu.Any(s=>s.Contains("始终保留",StringComparison.Ordinal)))&&outsideFolder.Files.Any(x=>x.Menu.Any(s=>s.Contains("释放空间",StringComparison.Ordinal)));report["OutsideNativeMenusAvailable"]=outsideMenus;
            var menuRequired=File.Exists(Path.Combine(area,"menu-profile"));
            var customMenus=outsideFolder.Files.Any(x=>x.Menu.Any(text=>text.Contains("HybridMount",StringComparison.Ordinal)));report["CustomMenusAvailable"]=customMenus;
            if(menuRequired)Assert.True(customMenus,"包外视图没有发现本轮自有菜单。");
            var properties=names.Select(name=>CloudShellState.Read(Path.Combine(test.Local,name))).ToArray();Assert.Equal(new uint?[]{1,2,3,1},properties.Select(x=>x.ProviderState));
            if(!empty){var outsideImages=outsideResult.GetProperty("Images").Deserialize<Dictionary<string,ImageResult>>()!;Assert.True(outsideImages["cloud.bmp"].Pattern&&outsideImages["released.bmp"].Pattern,"联机文件的系统图像工厂没有返回缓存识别像素。");}success=true;
        }
        catch(Exception error){report["Error"]=error.ToString();}
        finally
        {
            try
            {
                try{if(cacheRoot is not null){Assert.True(store!.DeleteDatabase(cacheRoot));Assert.False(File.Exists(store.Database));report["CacheCleanup"]=true;}}catch(Exception cleanup){report["CacheCleanupError"]=cleanup.ToString();success=false;}
                if(test is not null){if(test.Id==Guid.Empty&&test.Manager.Mounts.Count==1)test.Id=test.Manager.Mounts.Single().Definition.Id;if(test.Id!=Guid.Empty){var job=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"clearAndRemove"));report["Removal"]=job;Assert.Equal("Completed",job.State);}await test.Manager.DisposeAsync();await test.Logger.DisposeAsync();Registry.CurrentUser.DeleteSubKeyTree(test.Registry,false);await test.Dav.DisposeAsync();Assert.Null(CloudFilesNative.RootIdentity(test.Local,true));report["Cleanup"]=true;}
            }
            catch(Exception error){report["CleanupError"]=error.ToString();report["Cleanup"]=false;success=false;}
            report["Success"]=success;await Save(area,"result.json",report);
        }
        return success?0:1;
    }
    /// <summary>证据先完整写入同目录临时文件再原子发布，避免独立进程把存在误当成写入完成。</summary>
    private static Task Save(string area,string file,object report)
    {
        var target=Path.Combine(area,file);var temporary=target+".new";File.WriteAllText(temporary,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,target,true);return Task.CompletedTask;
    }
    private static object Facts(UploadFlowTests.Fixture test,string[] names)=>names.Select(name=>{var path=Path.Combine(test.Local,name);var info=CloudFilesNative.InspectPersisted(path);return new{Name=name,Stamp=LocalFileState.Read(path),Identity=Convert.ToBase64String(info.Identity),info.Info.PinState,info.Info.InSyncState,info.Info.OnDiskSize,info.Info.ValidatedSize,info.Info.ModifiedSize,Full=CloudFilesNative.HasFullContent(path,new FileInfo(path).Length)};}).ToArray();
    // 原始USN及ChangeTime保留在报告；Shell可能更新访问元数据，验收对照身份、内容状态、固定与水合范围。
    private static object Stable(object facts)=>JsonSerializer.SerializeToElement(facts).EnumerateArray().Select(row=>new{Identity=row.GetProperty("Identity").GetString(),Stamp=row.GetProperty("Stamp").GetProperty("Identity").GetString(),Length=row.GetProperty("Stamp").GetProperty("Length").GetInt64(),Modified=row.GetProperty("Stamp").GetProperty("LastWriteTime").GetInt64(),Pin=row.GetProperty("PinState").GetUInt32(),InSync=row.GetProperty("InSyncState").GetUInt32(),OnDisk=row.GetProperty("OnDiskSize").GetInt64(),Validated=row.GetProperty("ValidatedSize").GetInt64(),Dirty=row.GetProperty("ModifiedSize").GetInt64(),Full=row.GetProperty("Full").GetBoolean()}).ToArray();
    private sealed record ImageResult(int HResult,int Width,int Height,int PatternPixels,bool Pattern);
    /// <summary>真实Shell图像工厂THUMBNAILONLY，不直接创建本产品CLSID；像素来自DB而非实际正文。</summary>
    private static ImageResult Image(string path)
    {
        var status=NativePreviewInterop.ShellImage(path,64,8,out var bitmap);return DescribeImage(status,bitmap);
    }
    /// <summary>单文件强制提取仍由Windows决定处理器；与显式COM对照独立记录。</summary>
    internal static object ForcedImage(string path){var status=NativePreviewInterop.ShellCachedImage(path,64,out var bitmap);return DescribeImage(status,bitmap);}
    private static ImageResult DescribeImage(int status,nint bitmap)
    {
        if(status<0||bitmap==0)return new(status,0,0,0,false);
        try{var metadata=new byte[32];Assert.Equal(32,GetObject(bitmap,32,metadata));var width=BitConverter.ToInt32(metadata,4);var height=Math.Abs(BitConverter.ToInt32(metadata,8));Assert.InRange(width,1,256);Assert.InRange(height,1,256);var pixels=new byte[width*height*4];var info=new BitmapInfo{Size=40,Width=width,Height=-height,Planes=1,Bits=32};var dc=CreateCompatibleDC(0);try{Assert.Equal(height,GetDIBits(dc,bitmap,0,(uint)height,pixels,ref info,0));}finally{DeleteDC(dc);}var count=0;for(var offset=0;offset<pixels.Length;offset+=4)if(pixels[offset+2] is >=167 and <=173&&pixels[offset+3]>240)count++;return new(status,width,height,count,count>width*height*0.8);}
        finally{NativePreviewInterop.DeleteObject(bitmap);}
    }
    /// <summary>另行测量真实包COM激活，不能用它替代上面的Shell自动选择断言。</summary>
    private static unsafe object DirectImage(string path,Guid clsid)
    {
        var initialized=CoInitializeEx(0,2);nint init=0,item=0,thumb=0,bitmap=0;uint alpha=0;var hr=unchecked((int)0x80004005);
        try
        {
            var initId=new Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841");hr=CoCreateInstance(in clsid,0,4,in initId,out init);if(hr<0)return new{Stage="Activation",HResult=hr};
            var itemId=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");hr=SHCreateItemFromParsingName(path,0,in itemId,out item);if(hr<0)return new{Stage="Item",HResult=hr};
            hr=((delegate* unmanaged[Stdcall]<nint,nint,uint,int>)(*(void***)init)[3])(init,item,0);if(hr<0)return new{Stage="Initialize",HResult=hr};
            var thumbId=new Guid("E357FCCD-A995-4576-B01F-234630154E96");hr=Marshal.QueryInterface(init,in thumbId,out thumb);if(hr<0)return new{Stage="QueryInterface",HResult=hr};
            nint result=0;uint kind=0;hr=((delegate* unmanaged[Stdcall]<nint,uint,nint*,uint*,int>)(*(void***)thumb)[3])(thumb,64,&result,&kind);bitmap=result;alpha=kind;
            return new{Stage="Thumbnail",HResult=hr,Bitmap=bitmap!=0,Alpha=alpha};
        }
        catch(Exception error){return new{Stage="Exception",error.HResult,Type=error.GetType().Name};}
        finally{if(bitmap!=0)NativePreviewInterop.DeleteObject(bitmap);if(thumb!=0)Marshal.Release(thumb);if(item!=0)Marshal.Release(item);if(init!=0)Marshal.Release(init);if(initialized>=0)CoUninitialize();}
    }
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("ole32.dll")]private static extern int CoCreateInstance(in Guid clsid,nint outer,uint context,in Guid iid,out nint result);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHCreateItemFromParsingName(string path,nint context,in Guid iid,out nint item);
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public uint Size;public int Width,Height;public ushort Planes,Bits;public uint Compression,ImageSize;public int X,Y;public uint Used,Important;}
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")]private static extern int GetObject(nint bitmap,int length,byte[] bytes);
    [DllImport("gdi32.dll")]private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]private static extern int GetDIBits(nint dc,nint bitmap,uint start,uint count,byte[] pixels,ref BitmapInfo info,uint usage);
}
