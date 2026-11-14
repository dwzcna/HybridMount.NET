using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>独立MSIX退出CLR后的真实COM验收；所有创建、写入及删除都限制在本轮GUID根。</summary>
public static class AsyncShellAcceptanceProbe
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static void Save(string area,string name,object value)=>File.WriteAllText(Path.Combine(area,name),JsonSerializer.Serialize(value,Json));
    private static JsonElement Read(string area,string name)=>JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(area,name)));
    /// <summary>注册隔离根、冷占位和完整普通媒体后正常停止原生连接并退出；本阶段不调用缩略图。</summary>
    public static async Task<int> RegisterAsync(string area)
    {
        await NativeThumbnailBindingProbe.RegisterForUninstallAsync(area);var root=Read(area,"uninstall-root.json").GetProperty("Root").Deserialize<MountDefinition>()!;
        var local=Path.Combine(root.LocalPath,"async-local.bmp");var cold=Path.Combine(root.LocalPath,"async-cold.bmp");var cached=Path.Combine(root.LocalPath,"async-cached.bmp");var bytes=PreviewPipelineTests.Bmp(600,600);
        var runtime=NativeRuntimeClient.Get();var session=Guid.NewGuid().ToString("N");
        await runtime.InvokeAsync("connectRoot",NativeRuntimeArguments.Root(root,new{sessionId=session,blockHydration=true}));
        try{foreach(var item in new[]{(Path:cold,Id:"async-cold"),(Path:cached,Id:"async-cached")})await runtime.InvokeAsync("createPlaceholder",NativeRuntimeArguments.Root(root,new{sessionId=session,path=item.Path,entryId=item.Id,directory=false,size=(long)bytes.Length,time=DateTime.UtcNow.ToFileTimeUtc()}));}
        finally{await runtime.InvokeAsync("disconnectRoot",NativeRuntimeArguments.Root(root,new{sessionId=session}));await NativeRuntimeClient.StopSharedAsync();}
        await File.WriteAllBytesAsync(local,bytes);
        // 千文件目录用于实际Explorer响应测量；普通空文本不触发媒体解码或源读取。
        for(var i=0;i<996;i++)await File.WriteAllBytesAsync(Path.Combine(root.LocalPath,$"item-{i:D4}.txt"),[]);
        var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(root.Id));var grant=store.Configure(ThumbnailCacheReader.RootFor(root with{ThumbnailsEnabled=true}),true);
        var budget=new PreviewRangeBudget(bytes.Length,(offset,buffer,_)=>{bytes.AsMemory((int)offset,buffer.Length).CopyTo(buffer);return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));});
        var frames=await new PreviewWorkerClient().DecodeAsync(".bmp",bytes.Length,budget.ReadAsync);Assert.NotNull(frames);
        var binding=ThumbnailCacheReader.Capture(grant,new MountEntry("async-cached","async-cached.bmp","async-cached.bmp","probe61",false,Materialized:true,RemotePresence:RemotePresence.Present));Assert.NotNull(binding);Assert.True(store.Publish(binding,frames));
        Save(area,"async-ready.json",new{Root=root,Local=local,Cold=cold,Cached=cached,Grant=grant,Database=store.Database,Pid=Environment.ProcessId,Started=Process.GetCurrentProcess().StartTime.ToUniversalTime(),Installed=global::Windows.ApplicationModel.Package.Current.InstalledLocation.Path,Before=Facts(local,cold),Count=Directory.EnumerateFiles(root.LocalPath).Count()});return 0;
    }
    private static object Facts(string local,string cold){var state=CloudFilesNative.InspectPersisted(cold);return new{Local=LocalFileState.Read(local).Identity,Hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(local))),Cold=Convert.ToBase64String(state.Identity),state.Info.PinState,state.Info.InSyncState,state.Info.OnDiskSize,state.Info.ModifiedSize};}
    /// <summary>只对已登记的本轮根执行显式包COM、Shell自动图像和Explorer消息测量，输出原始样本。</summary>
    public static int Outside(string area)
    {
        var report=new Dictionary<string,object?>();var passed=false;
        try
        {
            var setup=Read(area,"async-ready.json");var root=setup.GetProperty("Root").Deserialize<MountDefinition>()!;var local=setup.GetProperty("Local").GetString()!;var cold=setup.GetProperty("Cold").GetString()!;
            Assert.StartsWith(Path.GetFullPath(area)+Path.DirectorySeparatorChar,Path.GetFullPath(root.LocalPath),StringComparison.OrdinalIgnoreCase);
            var owner=Process.GetProcesses().FirstOrDefault(x=>x.Id==setup.GetProperty("Pid").GetInt32()&&x.StartTime.ToUniversalTime()==setup.GetProperty("Started").GetDateTime());try{Assert.True(owner is null||owner.HasExited,"隔离提供程序仍在执行。");}finally{owner?.Dispose();}report["MainExited"]=true;
            var tag=Path.GetFileName(area)[^8..];var clsid=Guid.Parse(tag+"-4d1c-4c7e-a519-34c76b9bb281");
            using(var localCom=new Reader(local,clsid))
            {
                report["ColdActivationMs"]=localCom.ActivationMilliseconds;var samples=localCom.Samples(1001);report["LocalSamples"]=samples;Assert.False(samples[0].Hit);Assert.Contains(samples,x=>x.Hit);
                var hotSamples=localCom.Samples(1000);report["MemoryHitSamples"]=hotSamples;Assert.All(hotSamples,sample=>Assert.True(sample.Hit));var hot=hotSamples.Select(x=>x.Milliseconds).Order().ToArray();report["LocalP95"]=hot[(int)(hot.Length*.95)];report["LocalP99"]=hot[(int)(hot.Length*.99)];Assert.True(hot[(int)(hot.Length*.95)]<=50);Assert.True(hot[(int)(hot.Length*.99)]<=150);
                using(var coldCom=new Reader(cold,clsid)){var misses=coldCom.Samples(1000);report["ColdSamples"]=misses;Assert.All(misses,x=>Assert.False(x.Hit));}
                using(var cachedCom=new Reader(setup.GetProperty("Cached").GetString()!,clsid)){var hits=cachedCom.Samples(1000);report["PersistentHitSamples"]=hits;Assert.All(hits,sample=>Assert.True(sample.Hit));}
                report["AutomaticImage"]=typeof(NativeThumbnailBindingProbe).GetMethod("Image",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[local]);
                var store=new ThumbnailCacheStore(setup.GetProperty("Database").GetString()!);store.Configure(setup.GetProperty("Grant").Deserialize<ThumbnailCacheRoot>()! with{Enabled=false},true);
                var disabled=localCom.Samples(1000);report["DisabledSamples"]=disabled;Assert.All(disabled,x=>Assert.False(x.Hit));
            }
            var performance=new Dictionary<string,object>();foreach(var label in new[]{"LocalSamples","MemoryHitSamples","PersistentHitSamples","ColdSamples","DisabledSamples"})
            {
                var times=((Sample[])report[label]!).Select(sample=>sample.Milliseconds).Order().ToArray();var p95=times[(int)(times.Length*.95)];var p99=times[(int)(times.Length*.99)];performance[label]=new{Count=times.Length,P95=p95,P99=p99,Maximum=times[^1]};Assert.True(p95<=50,label+" P95="+p95);Assert.True(p99<=150,label+" P99="+p99);
            }
            report["Performance"]=performance;
            report["After"]=Facts(local,cold);Assert.True(JsonElement.DeepEquals(setup.GetProperty("Before"),JsonSerializer.SerializeToElement(report["After"],Json)));
            Assert.False(CloudFilesNative.HasFullContent(cold,new FileInfo(cold).Length));report["ColdRemainsOffline"]=true;
            var native=Process.GetProcessesByName("HybridMount.Stock").Where(p=>{try{return p.MainModule?.FileName?.StartsWith(setup.GetProperty("Installed").GetString()!,StringComparison.OrdinalIgnoreCase)==true;}catch{return false;}}).ToArray();
            report["NativeProcesses"]=native.Select(p=>new{p.Id,Clr=p.Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase)||m.ModuleName.Equals("clr.dll",StringComparison.OrdinalIgnoreCase))}).ToArray();Assert.NotEmpty(native);Assert.All(native,p=>Assert.DoesNotContain(p.Modules.Cast<ProcessModule>(),m=>m.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase)));
            new ThumbnailCacheStore(setup.GetProperty("Database").GetString()!).Configure(setup.GetProperty("Grant").Deserialize<ThumbnailCacheRoot>()! with{Enabled=true},true);
            report["Explorer"]=Explorer(root.LocalPath);passed=true;
        }
        catch(Exception error){report["Error"]=error.ToString();}
        report["Passed"]=passed;Save(area,"async-outside.json",report);return passed?0:1;
    }
    private sealed record Sample(double Milliseconds,int HResult,bool Hit);
    /// <summary>同一COM对象反复GetThumbnail，冷激活单独计时，所有位图及时归还。</summary>
    private sealed unsafe class Reader:IDisposable
    {
        private nint _init,_item,_thumb;private readonly bool _uninitialize;
        public double ActivationMilliseconds{get;}
        public Reader(string path,Guid clsid)
        {
            _uninitialize=CoInitializeEx(0,2)>=0;var watch=Stopwatch.StartNew();
            try{var iid=new Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841");Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid,0,4,in iid,out _init));iid=new("43826D1E-E718-42EE-BC55-A1E261C37BFE");Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path,0,in iid,out _item));Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint,uint,int>)(*(void***)_init)[3])(_init,_item,0));iid=new("E357FCCD-A995-4576-B01F-234630154E96");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_init,in iid,out _thumb));ActivationMilliseconds=watch.Elapsed.TotalMilliseconds;}
            catch{Dispose();throw;}
        }
        public Sample[] Samples(int count){var samples=new Sample[count];for(var i=0;i<count;i++){nint bitmap=0;uint kind=0;var watch=Stopwatch.StartNew();var hr=((delegate* unmanaged[Stdcall]<nint,uint,nint*,uint*,int>)(*(void***)_thumb)[3])(_thumb,64,&bitmap,&kind);samples[i]=new(watch.Elapsed.TotalMilliseconds,hr,hr>=0&&bitmap!=0);if(bitmap!=0)NativeThumbnailInterop.DeleteObject(bitmap);Thread.Sleep(10);}return samples;}
        public void Dispose(){if(_thumb!=0){Marshal.Release(_thumb);_thumb=0;}if(_item!=0){Marshal.Release(_item);_item=0;}if(_init!=0){Marshal.Release(_init);_init=0;}if(_uninitialize)CoUninitialize();}
    }
    /// <summary>只打开并关闭本轮千文件窗口；WM_NULL测量消息响应，不访问UIA子树。</summary>
    private static object Explorer(string path)
    {
        object shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;object? owned=null;
        try{((dynamic)shell).Explore(path);for(var i=0;i<60&&owned is null;i++){foreach(var window in ((dynamic)shell).Windows())try{if(string.Equals((string)window.Document.Folder.Self.Path,path,StringComparison.OrdinalIgnoreCase)){owned=window;break;}}catch(COMException){}if(owned is null)Thread.Sleep(100);}Assert.NotNull(owned);var hwnd=new IntPtr(Convert.ToInt64(((dynamic)owned).HWND));var samples=new List<double>();var timeouts=0;for(var i=0;i<1000;i++){var watch=Stopwatch.StartNew();if(SendMessageTimeout(hwnd,0,0,0,2,1000,out _)==0)timeouts++;samples.Add(watch.Elapsed.TotalMilliseconds);Thread.Sleep(10);}var sorted=samples.Order().ToArray();Assert.Equal(0,timeouts);Assert.True(sorted[(int)(sorted.Length*.95)]<=200);Assert.True(sorted[^1]<1000);return new{Samples=samples,P95=sorted[(int)(sorted.Length*.95)],P99=sorted[(int)(sorted.Length*.99)],Maximum=sorted[^1],Timeouts=timeouts};}
        finally{if(owned is not null){try{if(string.Equals((string)((dynamic)owned).Document.Folder.Self.Path,path,StringComparison.OrdinalIgnoreCase))((dynamic)owned).Quit();}catch(COMException){}Marshal.ReleaseComObject(owned);}Marshal.ReleaseComObject(shell);}
    }
    /// <summary>卸载前只清除本轮测试数据和缓存，保留原零字节占位供系统注销验收。</summary>
    public static int Cleanup(string area)
    {
        var row=Read(area,"async-ready.json");var root=row.GetProperty("Root").Deserialize<MountDefinition>()!;Assert.StartsWith(Path.GetFullPath(area)+Path.DirectorySeparatorChar,Path.GetFullPath(root.LocalPath),StringComparison.OrdinalIgnoreCase);Assert.True(JsonElement.DeepEquals(row.GetProperty("Before"),JsonSerializer.SerializeToElement(Facts(row.GetProperty("Local").GetString()!,row.GetProperty("Cold").GetString()!),Json)));
        var store=new ThumbnailCacheStore(row.GetProperty("Database").GetString()!);Assert.True(store.DeleteDatabase(store.Root(root.Id)));
        foreach(var file in Directory.EnumerateFiles(root.LocalPath)){var name=Path.GetFileName(file);if(name=="zero.bin")continue;Assert.True(name is "async-local.bmp" or "async-cold.bmp" or "async-cached.bmp"||name.StartsWith("item-",StringComparison.Ordinal)&&name.EndsWith(".txt",StringComparison.Ordinal)&&new FileInfo(file).Length==0);File.Delete(file);}Save(area,"async-cleanup.json",new{Passed=true});return 0;
    }
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("ole32.dll")]private static extern int CoCreateInstance(in Guid clsid,nint outer,uint context,in Guid iid,out nint result);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHCreateItemFromParsingName(string path,nint context,in Guid iid,out nint item);
    [DllImport("user32.dll")]private static extern nint SendMessageTimeout(nint hwnd,uint message,nint wparam,nint lparam,uint flags,uint timeout,out nint result);
}
