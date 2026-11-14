using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace HybridMount.Tests;

/// <summary>真实原生菜单以隔离根验证无等待状态、完整集合重验及有界生命周期；原生查询不读取源正文。</summary>
[Collection("Native IPC")]
public sealed class NativeMenuQueryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact(Timeout=15000)]
    public Task ColdFastStateDoesNotTouchEvenASlowShellSelection()=>Task.Run(()=>
    {
        using var menu=new Probe();using var selection=new Selection([@"C:\ordinary\unused.txt"]);selection.Value.DelayMilliseconds=250;
        var before=menu.Stats();var sample=Measure(()=>menu.State(2,selection,false));
        Assert.Equal(0,selection.Value.CountCalls);Assert.Equal(0,selection.Value.ItemCalls);Assert.Equal(before[3],menu.Stats()[3]);
        Assert.True(sample.P95<=50&&sample.P99<=150);output.WriteLine(JsonSerializer.Serialize(new{sample.P95,sample.P99,sample.Max,selection.Value.CountCalls,selection.Value.ItemCalls}));
    });
    [Fact(Timeout=30000)]
    public async Task SlowSamplingDoesNotEnterSynchronousCallsAndCachedOrPendingCallsMeetBudget()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);var path=Path.Combine(test.Local,"文件.bin");var requests=test.Dav.Server.Requests.Count;
        using var menu=new Probe();using var selection=new Selection([path]);menu.Delay(300);
        try
        {
            var before=menu.Stats();Assert.Equal(Pending,menu.State(2,selection,false).Code);Assert.Equal(before[3],menu.Stats()[3]);var cold=Stopwatch.StartNew();Assert.Equal(Pending,menu.State(2,selection,true).Code);cold.Stop();
            var pending=Measure(()=>menu.State(2,selection,false));var answer=await menu.Query(2,selection);Assert.Equal(0u,answer);menu.Delay(0);var queried=menu.Stats()[8];var hot=Measure(()=>menu.State(2,selection,false));Assert.Equal(queried,menu.Stats()[8]);
            Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(0,CloudFilesNative.Inspect(path)!.Value.Info.OnDiskSize);Assert.True(pending.P95<=20&&pending.P99<=50);Assert.True(hot.P95<=20&&hot.P99<=50);output.WriteLine(JsonSerializer.Serialize(new{ColdMarshalMs=cold.Elapsed.TotalMilliseconds,Pending=new{pending.P95,pending.P99,pending.Max},Cached=new{hot.P95,hot.P99,hot.Max},Counters=menu.Stats()}));
        }
        finally{menu.Delay(0);}
    }
    [Fact(Timeout=30000)]
    public async Task RootIdentitySummaryCanBeEnrichedForColdCacheActions()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);using var menu=new Probe();using var selection=new Selection([Path.Combine(test.Local,"文件.bin")]);var source=test.Dav.Server.Requests.Count;
        Assert.Equal(0u,await menu.Query(0,selection));Assert.Equal(0u,await menu.Query(2,selection));Assert.NotEqual(0u,await menu.Query(1,selection));Assert.Equal(0u,await menu.Query(3,selection));Assert.Equal(source,test.Dav.Server.Requests.Count);
    }
    [Fact(Timeout=30000)]
    public async Task SeventeenthForeignItemRejectsWholeSelectionAndNeverDispatchesPartialTargets()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);var paths=await Files(test,20);var outside=Path.Combine(test.Dav.Area,"foreign.txt");await File.WriteAllTextAsync(outside,"outside");paths[16]=outside;
        using var menu=new Probe();using var selection=new Selection(paths);var received=0;await using var server=new IpcServerService(test.Logger,NativeThumbnailInterop.PipeName);server.RequestReceived+=_=>Interlocked.Increment(ref received);server.Start();
        Assert.NotEqual(0u,await menu.Query(0,selection));var before=menu.Stats();Assert.Equal(0,menu.Invoke(3,selection));await Until(()=>menu.Stats()[10]>before[10]);Assert.Equal(0,received);Assert.Equal("outside",await File.ReadAllTextAsync(outside));Assert.InRange(menu.Stats()[8]-before[8],16UL,16UL);
    }
    [Fact(Timeout=30000)]
    public async Task InvokeRechecksAllFortyTargetsAndPreservesSelectionPaths()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);var paths=await Files(test,40);using var menu=new Probe();using var selection=new Selection(paths);var requests=test.Dav.Server.Requests.Count;
        await using var server=new IpcServerService(test.Logger,NativeThumbnailInterop.PipeName);var received=new TaskCompletionSource<IpcRequest>(TaskCreationOptions.RunContinuationsAsynchronously);server.RequestReceived+=value=>received.TrySetResult(value);server.Start();
        Assert.Equal(0u,await menu.Query(3,selection));var inspected=menu.Stats()[8];Assert.Equal(0,menu.Invoke(3,selection));var request=await received.Task.WaitAsync(TimeSpan.FromSeconds(8));Assert.Equal("showSyncIssues",request.Operation);Assert.Equal(paths,request.Paths);Assert.True(menu.Stats()[8]-inspected>=40);Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }
    [Fact(Timeout=30000)]
    public async Task ReplacedIdentityBetweenMenuAndInvokeIsRejected()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);var path=(await Files(test,1))[0];using var menu=new Probe();using var selection=new Selection([path]);Assert.Equal(0u,await menu.Query(3,selection));var before=LocalFileState.Read(path);var dispatched=menu.Stats()[9];
        File.Delete(path);await File.WriteAllTextAsync(path,"new identity");Assert.NotEqual(before.Identity,LocalFileState.Read(path).Identity);var count=menu.Stats()[10];Assert.Equal(0,menu.Invoke(3,selection));await Until(()=>menu.Stats()[10]>count);Assert.Equal("new identity",await File.ReadAllTextAsync(path));Assert.Equal(dispatched,menu.Stats()[9]);
    }
    [Fact(Timeout=30000)]
    public async Task QueryQueueIsBoundedAndCanceledOwnersDrainWithoutLatePublication()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);using var master=new Probe();using var selection=new Selection([Path.Combine(test.Local,"文件.bin")]);var probes=new List<Probe>();master.Delay(1000);var initial=master.Stats();
        try{for(var i=0;i<32;i++){var probe=new Probe();probes.Add(probe);Assert.Equal(Pending,probe.State(2,selection,true).Code);}var stats=master.Stats();Assert.True(stats[4]>initial[4]);Assert.InRange(stats[7],1UL,16UL);Assert.InRange(stats[13],1UL,16UL);}
        finally{master.Delay(0);foreach(var probe in probes)probe.Dispose();}
        await Until(()=>master.Stats()[13]==0);Assert.True(master.Stats()[5]==0);Assert.Equal(0u,await master.Query(2,selection));Assert.Equal(0,CloudFilesNative.Inspect(Path.Combine(test.Local,"文件.bin"))!.Value.Info.OnDiskSize);
    }
    [Fact(Timeout=30000)]
    public async Task DuplicateAndOversizeSelectionsStayConservative()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);using var menu=new Probe();var path=Path.Combine(test.Local,"文件.bin");using var duplicate=new Selection([path,path]);Assert.NotEqual(0u,await menu.Query(0,duplicate));using var oversized=new Selection(Enumerable.Repeat(path,1025).ToArray());Assert.NotEqual(0u,await menu.Query(0,oversized));
    }
    private static async Task<string[]> Files(UploadFlowTests.Fixture test,int count)
    {var paths=Enumerable.Range(0,count).Select(i=>Path.Combine(test.Local,"menu-"+i+".txt")).ToArray();foreach(var path in paths)await File.WriteAllTextAsync(path,"menu metadata");return paths;}
    private static async Task Until(Func<bool> ready){using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(8));while(!ready())await Task.Delay(5,stop.Token);}
    private const int Pending=unchecked((int)0x8000000a);
    private static (double P95,double P99,double Max) Measure(Func<(int Code,uint State)> call)
    {var samples=new double[1000];for(var i=0;i<samples.Length;i++){var before=Stopwatch.GetTimestamp();var value=call();Assert.True(value.Code is 0 or Pending);samples[i]=Stopwatch.GetElapsedTime(before).TotalMilliseconds;}Array.Sort(samples);return(samples[949],samples[989],samples[^1]);}

    /// <summary>每个同步桥接调用在当前MTA初始化，使用MTA usage cookie跨异步续体保持封送所属公寓。</summary>
    private sealed class Probe:IDisposable
    {
        private readonly nint _library,_mta;private nint _factory,_root;private readonly nint[] _children=new nint[3];private static int _live;
        internal Probe()
        {
            Assert.Equal(0,CoIncrementMTAUsage(out _mta));using var apartment=new Apartment();_library=NativeLibrary.Load(NativeThumbnailInterop.LibraryPath);Interlocked.Increment(ref _live);
            unsafe{var get=(delegate* unmanaged[Stdcall]<Guid*,Guid*,nint*,int>)NativeLibrary.GetExport(_library,"DllGetClassObject");var clsid=new Guid("59964FC1-D076-4DC1-9778-01F5833A5D4A");var factoryId=new Guid("00000001-0000-0000-C000-000000000046");nint factory=0;Assert.Equal(0,get(&clsid,&factoryId,&factory));_factory=factory;var commandId=new Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9");nint root=0;Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,nint,Guid*,nint*,int>)Table(_factory)[3])(_factory,0,&commandId,&root));_root=root;nint enumerator=0;Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,nint*,int>)Table(root)[10])(root,&enumerator));try{for(var i=0;i<3;i++){nint child=0;uint fetched=0;Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,uint,nint*,uint*,int>)Table(enumerator)[3])(enumerator,1,&child,&fetched));_children[i]=child;}}finally{Release(enumerator);}}
        }
        internal unsafe (int Code,uint State) State(int action,Selection selection,bool slow)
        {using var apartment=new Apartment();var command=action==0?_root:_children[action-1];uint state=99;var code=((delegate* unmanaged[Stdcall]<nint,nint,int,uint*,int>)Table(command)[7])(command,selection.Pointer,slow?1:0,&state);return(code,state);}
        internal async Task<uint> Query(int action,Selection selection)
        {var until=Environment.TickCount64+7000;while(true){var value=State(action,selection,true);if(value.Code==0)return value.State;Assert.Equal(Pending,value.Code);if(Environment.TickCount64>=until)throw new TimeoutException("菜单摘要没有完成");await Task.Delay(2);}}
        internal unsafe int Invoke(int action,Selection selection){using var apartment=new Apartment();return ((delegate* unmanaged[Stdcall]<nint,nint,nint,int>)Table(_children[action-1])[8])(_children[action-1],selection.Pointer,0);}
        internal unsafe ulong[] Stats(){var values=new ulong[14];fixed(ulong* buffer=values)Assert.Equal(0,((delegate* unmanaged[Stdcall]<ulong*,uint,int>)NativeLibrary.GetExport(_library,"HybridMountMenuDiagnostics"))(buffer,14));return values;}
        internal unsafe void Delay(uint milliseconds)=>Assert.Equal(0,((delegate* unmanaged[Stdcall]<uint,int>)NativeLibrary.GetExport(_library,"HybridMountMenuTestDelay"))(milliseconds));
        private static unsafe void** Table(nint instance)=>*(void***)instance;
        private static unsafe void Release(nint instance){if(instance!=0)((delegate* unmanaged[Stdcall]<nint,uint>)Table(instance)[2])(instance);}
        public unsafe void Dispose(){using var apartment=new Apartment();foreach(var child in _children)Release(child);Release(_root);Release(_factory);if(Interlocked.Decrement(ref _live)==0){var until=Environment.TickCount64+7000;var unload=(delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(_library,"DllCanUnloadNow");while(unload()!=0&&Environment.TickCount64<until)Thread.Sleep(2);Assert.Equal(0,unload());}NativeLibrary.Free(_library);CoDecrementMTAUsage(_mta);}
    }
    private sealed class Apartment:IDisposable{private readonly int _code=CoInitializeEx(0,0);public Apartment(){if(_code<0)Marshal.ThrowExceptionForHR(_code);}public void Dispose(){if(_code>=0)CoUninitialize();}}
    private sealed class Selection:IDisposable
    {private readonly MenuSelection _value;internal MenuSelection Value=>_value;internal nint Pointer{get;}internal Selection(string[] paths){using var apartment=new Apartment();_value=new(paths);Pointer=Marshal.GetComInterfaceForObject(_value,typeof(IMenuSelection));}public void Dispose(){using var apartment=new Apartment();Marshal.Release(Pointer);GC.KeepAlive(_value);}}
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("ole32.dll")]private static extern int CoIncrementMTAUsage(out nint cookie);
    [DllImport("ole32.dll")]private static extern int CoDecrementMTAUsage(nint cookie);
}

/// <summary>测试选择只提供已有路径，不替代任何CFAPI或文件属性核验。</summary>
[ComVisible(true),Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMenuSelection
{
    [PreserveSig]int BindToHandler(nint context,in Guid handler,in Guid iid,out nint result);
    [PreserveSig]int GetPropertyStore(uint flags,in Guid iid,out nint result);
    [PreserveSig]int GetPropertyDescriptionList(in Guid key,in Guid iid,out nint result);
    [PreserveSig]int GetAttributes(uint flags,uint mask,out uint result);
    [PreserveSig]int GetCount(out uint count);
    [PreserveSig]int GetItemAt(uint index,out nint result);
    [PreserveSig]int EnumItems(out nint result);
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class MenuSelection(string[] paths):IMenuSelection
{
    public int CountCalls,ItemCalls,DelayMilliseconds;
    public int BindToHandler(nint context,in Guid handler,in Guid iid,out nint result){result=0;return unchecked((int)0x80004001);}
    public int GetPropertyStore(uint flags,in Guid iid,out nint result){result=0;return unchecked((int)0x80004001);}
    public int GetPropertyDescriptionList(in Guid key,in Guid iid,out nint result){result=0;return unchecked((int)0x80004001);}
    public int GetAttributes(uint flags,uint mask,out uint result){result=0;return unchecked((int)0x80004001);}
    public int GetCount(out uint count){Interlocked.Increment(ref CountCalls);if(DelayMilliseconds>0)Thread.Sleep(DelayMilliseconds);count=(uint)paths.Length;return 0;}
    public int GetItemAt(uint index,out nint result){Interlocked.Increment(ref ItemCalls);if(DelayMilliseconds>0)Thread.Sleep(DelayMilliseconds);result=0;if(index>=paths.Length)return unchecked((int)0x80070057);result=Marshal.GetComInterfaceForObject(new IdentityShellItem(paths[index]),typeof(IIdentityShellItem));return 0;}
    public int EnumItems(out nint result){result=0;return unchecked((int)0x80004001);}
}
