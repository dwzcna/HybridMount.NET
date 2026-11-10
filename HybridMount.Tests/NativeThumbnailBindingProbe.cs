using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Services.Mounting;
using Microsoft.Win32;
namespace HybridMount.Tests;

/// <summary>显式临时MSIX的原生自动绑定验收；缓存像素与正文不同，不能用普通图标或显式COM冒充通过。</summary>
public static class NativeThumbnailBindingProbe
{
    /// <summary>只使用本轮GUID根和回环源；包外读取结束后注销，卸载测试另建保留根。</summary>
    public static async Task<int> RunAsync(string area)
    {
        Directory.CreateDirectory(area);var report=new Dictionary<string,object?>();UploadFlowTests.Fixture? test=null;ThumbnailCacheStore? store=null;ThumbnailCacheRoot? cache=null;var success=false;
        try
        {
            var generation=File.Exists(Path.Combine(area,"generation-profile"));var observe=File.Exists(Path.Combine(area,"observe-explorer"));var package=global::Windows.ApplicationModel.Package.Current;Assert.StartsWith("dwzcn.HybridMount.NativePreview.Probe",package.Id.Name);report["Package"]=package.Id.FullName;
            test=new();test.Local=Path.Combine(area,"root-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(test.Local);test.Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
            test.Repository=new(Path.Combine(area,"data"),test.Registry);test.Logger=new(Path.Combine(area,"log.db"));test.Manager=new(test.Repository,test.Logger){LocalPreviewsForTests=generation,RemoteThumbnailsForTests=generation,BrowsePreviewsForTests=generation,ObserveExplorerForTests=observe};
            var names=new[]{"cold-"+Guid.NewGuid().ToString("N")+".bmp","local-"+Guid.NewGuid().ToString("N")+".bmp"};
            var bytes=PreviewPipelineTests.Bmp();for(var i=54;i<bytes.Length;i+=3)bytes[i+2]=30;foreach(var name in names)test.Dav.Put(name,bytes);var lateName="late-"+Guid.NewGuid().ToString("N")+".bmp";if(generation)test.Dav.Put(lateName,PreviewPipelineTests.Bmp());
            MountDefinition mount;
            try{mount=await test.Manager.AddAsync("Thumbnail-00945-"+Guid.NewGuid().ToString("N"),test.Local,false,source:test.Dav.Options,credentials:new("user","app-password"),autoUpload:false);}
            catch(COMException error)when(error.HResult==unchecked((int)0x80070490)&&test.Manager.Mounts.Count==1){test.Id=test.Manager.Mounts.Single().Definition.Id;await Task.Delay(3000);await test.Manager.ConnectAsync(test.Id);mount=test.Manager.Mounts.Single().Definition;}
            test.Id=mount.Id;await test.Manager.SetSchedulingForTestsAsync(test.Id,false);report["Root"]=mount;report["Registry"]=test.Registry;report["RootId"]=CloudRootRegistration.Id(mount);report["Names"]=names;report["LateName"]=generation?lateName:null;
            Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[Path.Combine(test.Local,names[1])]))).State);
            var pattern=PreviewPipelineTests.Bmp();var budget=new PreviewRangeBudget(pattern.Length,(offset,buffer,_)=>{pattern.AsMemory((int)offset,buffer.Length).CopyTo(buffer);return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));});
            var frames=await new PreviewWorkerClient().DecodeAsync(".bmp",pattern.Length,budget.ReadAsync);Assert.NotNull(frames);
            store=new(ThumbnailCachePaths.ForMount(mount.Id));cache=store.Configure(ThumbnailCacheReader.RootFor(mount with{ThumbnailsEnabled=true}));
            report["Entries"]=test.Repository.Entries(test.Id);report["CacheRoot"]=cache;
            foreach(var entry in test.Repository.Entries(test.Id).Where(x=>!x.IsDirectory&&names.Contains(x.LocalPath)))
            {
                var binding=ThumbnailCacheReader.Capture(cache,entry);if(binding is null){report["CaptureFailure"]=new{entry,File=CloudFilesNative.Inspect(RelativePaths.ToLocal(test.Local,entry.LocalPath)),Stamp=LocalFileState.Read(RelativePaths.ToLocal(test.Local,entry.LocalPath))};throw new IOException("缓存绑定捕获失败，保留精确条目证据。");}
                Assert.True(store.Publish(binding,frames));
            }
            using(var key=Registry.LocalMachine.OpenSubKey(RecoveryShellRegistration.Manager+"\\"+CloudRootRegistration.Id(mount))){report["RootValues"]=key?.GetValueNames().ToDictionary(x=>x,x=>key.GetValue(x));report["AUMID"]=key?.GetValue("AUMID");report["Namespace"]=key?.GetValue("NamespaceCLSID");Assert.Equal(package.Id.FamilyName+"!App",key?.GetValue("AUMID"));}
            if(generation)await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);
            var observationNumber=0;if(observe)NativeRuntimeClient.Get().EventReceived+=message=>{if(message.Event=="previewDemand"){Save(area,"observer-last.json",message.Data);var number=Interlocked.Increment(ref observationNumber);if(number<=128)Save(area,$"observer-{number:D3}.json",new{Utc=DateTime.UtcNow,Data=message.Data});}};report["NativeIdentity"]=await NativeRuntimeClient.Get().InvokeAsync("ping",new{});report["ProcessId"]=Environment.ProcessId;report["CacheDatabase"]=store.Database;report["CacheFileId"]=MountPathGuard.FileIdentity(store.Database);report["Before"]=Facts(test,names);report["RequestsBefore"]=test.Dav.Server.Requests.Count;
            Save(area,"ready.json",report);var until=DateTime.UtcNow.AddSeconds(90);var started=false;
            while(!File.Exists(Path.Combine(area,"outside.json"))&&DateTime.UtcNow<until)
            {
                if(generation&&File.Exists(Path.Combine(area,"start-generation"))&&!started){if(!observe)test.Manager.ApplyPreviewDemandForTests(test.Id,[Path.Combine(test.Local,lateName)]);started=true;}
                if(started&&!File.Exists(Path.Combine(area,"late-ready.json"))&&test.Manager.GetPreview(Path.Combine(test.Local,lateName),64) is not null&&test.Manager.PreviewState(Path.Combine(test.Local,lateName))!="queued")Save(area,"late-ready.json",new{Ready=true,Full=CloudFilesNative.HasFullContent(Path.Combine(test.Local,lateName),new FileInfo(Path.Combine(test.Local,lateName)).Length)});
                await Task.Delay(50);
            }
            Assert.True(File.Exists(Path.Combine(area,"outside.json")));report["Outside"]=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(area,"outside.json")));
            report["Images"]=names.ToDictionary(x=>x,x=>Image(Path.Combine(test.Local,x)));report["Folder"]=NativeShellInspector.Read(test.Local,test.Local,names);
            report["After"]=Facts(test,names);report["RequestsAfter"]=test.Dav.Server.Requests.Count;if(!generation)Assert.Equal(report["RequestsBefore"],report["RequestsAfter"]);
            else {Assert.False(CloudFilesNative.HasFullContent(Path.Combine(test.Local,lateName),new FileInfo(Path.Combine(test.Local,lateName)).Length));report["PreviewReads"]=test.Dav.Server.Requests.Where(x=>x.Method=="GET"&&x.Target.Contains(lateName,StringComparison.Ordinal)).Select(x=>new{x.Method,x.Target,Range=x.Headers.GetValueOrDefault("Range")}).ToArray();}
            success=((JsonElement)report["Outside"]!).GetProperty("Passed").GetBoolean();
        }
        catch(Exception error){report["Error"]=error.ToString();}
        finally
        {
            try{if(cache is not null)store!.DeleteDatabase(cache);if(test is not null){if(test.Id==Guid.Empty&&test.Manager.Mounts.Count==1)test.Id=test.Manager.Mounts.Single().Definition.Id;await test.DisposeAsync();}report["Cleanup"]=true;}
            catch(Exception error){report["Cleanup"]=false;report["CleanupError"]=error.ToString();success=false;}
            try{var tag=global::Windows.ApplicationModel.Package.Current.Id.Name[^8..];using var trace=Registry.CurrentUser.OpenSubKey(@"Software\HybridMount.NET.Tests\ShellProbe\"+tag);report["NativeTrace"]=trace?.GetValueNames().ToDictionary(x=>x,x=>trace.GetValue(x));Registry.CurrentUser.DeleteSubKeyTree(@"Software\HybridMount.NET.Tests\ShellProbe\"+tag,false);}catch(Exception error){report["TraceError"]=error.GetType().Name;}
            report["Passed"]=success;Save(area,"result.json",report);
        }
        return success?0:1;
    }
    /// <summary>独立非打包进程读取同一物理缓存、系统菜单与图像；不直接激活本产品处理器。</summary>
    public static int Outside(string area)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(area,"ready.json")));var row=doc.RootElement;var path=row.GetProperty("Root").GetProperty("LocalPath").GetString()!;var names=row.GetProperty("Names").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        // WinRT注册返回后Shell目录缓存异步失效；隔离首读保留明确时间，避免把传播窗口当作绑定失败。
        Thread.Sleep(3000);var images=names.ToDictionary(x=>x,x=>Image(Path.Combine(path,x)));var folder=NativeShellInspector.Read(path,path,names);
        var props=names.Select(x=>CloudShellState.Read(Path.Combine(path,x))).ToArray();var native=folder.Files.Any(x=>x.Menu.Any(t=>t.Contains("始终保留",StringComparison.Ordinal)))&&folder.Files.Any(x=>x.Menu.Any(t=>t.Contains("释放空间",StringComparison.Ordinal)));
        using var rootKey=Registry.LocalMachine.OpenSubKey(RecoveryShellRegistration.Manager+"\\"+row.GetProperty("RootId").GetString());
        var outsideRoot=rootKey?.GetValueNames().ToDictionary(x=>x,x=>rootKey.GetValue(x));
        var tag=Path.GetFileName(area)[^8..];var clsid=Guid.Parse(tag+"-4d1c-4c7e-a519-34c76b9bb281");
        JsonElement? lateBefore=null,lateAfter=null;
        if(row.TryGetProperty("LateName",out var late)&&late.ValueKind==JsonValueKind.String)
        {
            var latePath=Path.Combine(path,late.GetString()!);lateBefore=Image(latePath);Save(area,"start-generation",new{Started=true});
            var observe=File.Exists(Path.Combine(area,"observe-explorer"));object? shell=null;
            if(observe){shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);((dynamic)shell!).Open(path);
                for(var attempt=0;attempt<40;attempt++){var found=false;foreach(var window in ((dynamic)shell).Windows())try{if(string.Equals((string)window.Document.Folder.Self.Path,path,StringComparison.OrdinalIgnoreCase)){window.Visible=true;window.Width=900;window.Height=600;window.Document.CurrentViewMode=5;var hwnd=new IntPtr(Convert.ToInt64(window.HWND));ShowWindow(hwnd,9);Save(area,"explorer-window.json",new{Handle=hwnd.ToInt64(),Visible=IsWindowVisible(hwnd),Minimized=IsIconic(hwnd)});found=true;}}catch(COMException){}if(found)break;Thread.Sleep(100);}}

            try{
            var until=DateTime.UtcNow.AddSeconds(20);while(!File.Exists(Path.Combine(area,"late-ready.json"))&&DateTime.UtcNow<until)Thread.Sleep(50);
            if(!File.Exists(Path.Combine(area,"late-ready.json")))Save(area,"outside.json",new{Passed=false,Failure="browse-generation-timeout"});Assert.True(File.Exists(Path.Combine(area,"late-ready.json")),"浏览生成未完成。");lateAfter=Image(latePath);
            }finally{if(shell is not null){try{foreach(var window in ((dynamic)shell).Windows())try{if(string.Equals((string)window.Document.Folder.Self.Path,path,StringComparison.OrdinalIgnoreCase))window.Quit();}catch(COMException){}}finally{Marshal.ReleaseComObject(shell);}}}
        }
        var forced=names.ToDictionary(x=>x,x=>ThumbnailPackageProbe.ForcedImage(Path.Combine(path,x)));
        var direct=typeof(ThumbnailPackageProbe).GetMethod("DirectImage",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[Path.Combine(path,names[0]),clsid]);
        var passed=images.Values.All(x=>x.GetProperty("Pattern").GetBoolean())&&native&&props.Select(x=>x.ProviderState).SequenceEqual(new uint?[]{1,3})&&(lateAfter is null||lateAfter.Value.GetProperty("Pattern").GetBoolean());
        Save(area,"outside.json",new{Passed=passed,Images=images,LateBefore=lateBefore,LateAfter=lateAfter,ForcedImages=forced,Direct=direct,OutsideRoot=outsideRoot,Folder=folder,Properties=props,NativeMenus=native,CacheFileId=MountPathGuard.FileIdentity(row.GetProperty("CacheDatabase").GetString()!)});
        return passed?0:1;
    }
    /// <summary>只登记本轮零字节测试根并正常退出，保留注册供系统卸载收尾；不创建凭据或缓存。</summary>
    public static async Task<int> RegisterForUninstallAsync(string area)
    {
        var path=Path.Combine(area,"uninstall-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        var mount=new MountDefinition(Guid.NewGuid(),"Uninstall-00945-"+Guid.NewGuid().ToString("N"),path,RootIdentity:MountPathGuard.FileIdentity(path));
        await CloudRootRegistration.EnsureAsync(mount,default);var runtime=NativeRuntimeClient.Get();var session=Guid.NewGuid().ToString("N");
        await runtime.InvokeAsync("connectRoot",NativeRuntimeArguments.Root(mount,new{sessionId=session,blockHydration=true}));
        await runtime.InvokeAsync("createPlaceholder",NativeRuntimeArguments.Root(mount,new{sessionId=session,path=Path.Combine(path,"zero.bin"),entryId="uninstall-zero",directory=false,size=0L,time=DateTime.UtcNow.ToFileTimeUtc()}));
        var id=CloudRootRegistration.Id(mount);using var key=Registry.LocalMachine.OpenSubKey(RecoveryShellRegistration.Manager+"\\"+id);
        var proof=new{Root=mount,RootId=id,Namespace=key?.GetValue("NamespaceCLSID"),AUMID=key?.GetValue("AUMID"),FileId=MountPathGuard.FileIdentity(Path.Combine(path,"zero.bin"))};
        await runtime.InvokeAsync("disconnectRoot",NativeRuntimeArguments.Root(mount,new{sessionId=session}));await NativeRuntimeClient.StopSharedAsync();Save(area,"uninstall-root.json",proof);return 0;
    }
    /// <summary>包外核实系统注销、根注册及命名入口，随后只删除本轮已确认的零字节测试内容。</summary>
    public static async Task<int> VerifyUninstallAsync(string area)
    {
        Assert.False(WinRegisterService.IsPackaged());using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(area,"uninstall-root.json")));var row=doc.RootElement;var root=row.GetProperty("Root").Deserialize<MountDefinition>()!;
        Assert.StartsWith(Path.GetFullPath(area)+Path.DirectorySeparatorChar,Path.GetFullPath(root.LocalPath),StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.GetFileName(root.LocalPath).StartsWith("uninstall-",StringComparison.Ordinal)&&Guid.TryParseExact(Path.GetFileName(root.LocalPath)[10..],"N",out _));
        var native=CloudFilesNative.RootIdentity(root.LocalPath,true);using var registration=Registry.LocalMachine.OpenSubKey(RecoveryShellRegistration.Manager+"\\"+row.GetProperty("RootId").GetString());var space=row.GetProperty("Namespace").GetString();
        using var ns=space is null?null:Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\"+space);
        var passed=native is null&&registration is null&&ns is null;Save(area,"uninstall-proof.json",new{Passed=passed,NativeRootAbsent=native is null,RegistrationAbsent=registration is null,NamespaceAbsent=ns is null,root.LocalPath,RootId=row.GetProperty("RootId").GetString()});
        if(passed){var files=Directory.GetFiles(root.LocalPath);Assert.InRange(files.Length,0,1);if(files.Length!=0){Assert.Equal("zero.bin",Path.GetFileName(files[0]));Assert.Equal(row.GetProperty("FileId").GetString(),MountPathGuard.FileIdentity(files[0]));Assert.Equal(0,new FileInfo(files[0]).Length);File.Delete(files[0]);}Assert.Empty(Directory.EnumerateFileSystemEntries(root.LocalPath));Directory.Delete(root.LocalPath);}
        await NativeRuntimeClient.StopSharedAsync();return passed?0:1;
    }
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint window);
    private static JsonElement Image(string path)=>JsonSerializer.SerializeToElement(typeof(ThumbnailPackageProbe).GetMethod("Image",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[path]));
    private static object Facts(UploadFlowTests.Fixture test,string[] names)=>names.Select(name=>{var path=Path.Combine(test.Local,name);var info=CloudFilesNative.InspectPersisted(path);return new{Name=name,Stamp=LocalFileState.Read(path),Identity=Convert.ToBase64String(info.Identity),info.Info.PinState,info.Info.InSyncState,info.Info.OnDiskSize,info.Info.ModifiedSize,Full=CloudFilesNative.HasFullContent(path,new FileInfo(path).Length)};}).ToArray();
    private static void Save(string area,string file,object report){var path=Path.Combine(area,file);File.WriteAllText(path+".new",JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".new",path,true);}
}
