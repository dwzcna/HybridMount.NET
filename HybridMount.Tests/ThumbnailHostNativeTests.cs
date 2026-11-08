using System.Runtime.InteropServices;
using Microsoft.Win32;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>使用独立CLSID的原生本地COM宿主验证ABI、回调和生存期；不等同于Explorer自动选择或GUI验收。</summary>
[Collection("Native IPC")]
public sealed class ThumbnailHostNativeTests
{
    [Fact(Timeout=90000)]
    public async Task LocalComCacheHitScalesBitmapAndNeverOpensCloudContent()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Dav.Put("native.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"native.bmp");var before=CloudFilesNative.Inspect(path)!.Value;var count=test.Dav.Server.Requests.Count;
        await Mta(()=>
        {
            using var host=new Host((_,_)=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png")),200);
            using var provider=host.Create(path);Assert.Equal(0,provider.Thumbnail(16,out var alpha,out var bitmap));
            try{Assert.Equal(2u,alpha);Assert.Equal(16,NativePreviewInterop.BitmapWidth(bitmap));}finally{NativePreviewInterop.DeleteObject(bitmap);}
            Thread.Sleep(450);Assert.False(host.Exited); // 活动对象存在时不能按空闲超时释放回调。
            Assert.Equal(0,provider.Thumbnail(80,out _,out bitmap));try{Assert.InRange(NativePreviewInterop.BitmapWidth(bitmap),1,80);}finally{NativePreviewInterop.DeleteObject(bitmap);}
        });
        var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);Assert.Equal(count,test.Dav.Server.Requests.Count);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
    }
    [Theory(Timeout=90000)]
    [InlineData(false)][InlineData(true)]
    public async Task CacheMissAndDamagedPngNeverReturnSuccessfulGenericThumbnail(bool malformed)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Dav.Put("miss.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"miss.bmp");
        await Mta(()=>{using var host=new Host((_,_)=>malformed?new byte[64]:null,500);using var provider=host.Create(path);Assert.True(provider.Thumbnail(64,out var alpha,out var bitmap)<0);Assert.Equal(0u,alpha);Assert.Equal(0,bitmap);});
        Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="GET");
    }
    [Fact(Timeout=90000)]
    public async Task StopDuringAcceptedCallbackDoesNotReleaseItsContextOrObject()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Dav.Put("lifetime.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"lifetime.bmp");
        await Mta(()=>
        {
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            using var host=new Host((_,_)=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png"));},500);
            using var provider=host.Create(path);
            var stopping=Task.Run(()=>{try{Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));host.Stop();GC.Collect();Thread.Sleep(100);Assert.False(host.Exited);}finally{release.Set();}});
            Assert.Equal(0,provider.Thumbnail(32,out _,out var bitmap));NativePreviewInterop.DeleteObject(bitmap);stopping.GetAwaiter().GetResult();
        });
    }
    private static Task Mta(Action action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>{var initialized=CoInitializeEx(0,0);try{if(initialized<0)Marshal.ThrowExceptionForHR(initialized);action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{if(initialized>=0)CoUninitialize();}}){IsBackground=true};thread.SetApartmentState(ApartmentState.MTA);thread.Start();return done.Task;
    }
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    private sealed unsafe class Host:IDisposable
    {
        internal static readonly Guid ClassId=new("d0037037-4d1c-4c7e-a519-34c76b9bb281");
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Read(nint context,[MarshalAs(UnmanagedType.LPWStr)]string path,uint size,out nint png,out uint length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Run(Read read,nint context,nint stop,uint idle);
        private readonly nint _library;private readonly Read _read;private readonly EventWaitHandle _stop=new(false,EventResetMode.ManualReset);private readonly TaskCompletionSource<int> _exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Exited=>_exit.Task.IsCompleted;
        internal Host(Func<string,uint,byte[]?> read,uint idle)
        {
            var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;Assert.NotNull(root);
#if DEBUG
            const string configuration="Debug";
#else
            const string configuration="Release";
#endif
            var path=Path.Combine(root.FullName,"artifacts","native-thumbnail-host",RuntimeInformation.ProcessArchitecture==Architecture.Arm64?"ARM64":"x64",configuration,"HybridMount.Ext.dll");Assert.True(File.Exists(path),"先构建隔离缩略图宿主测试DLL："+path);_library=NativeLibrary.Load(path);
            _read=(nint context,string file,uint size,out nint png,out uint length)=>{png=0;length=0;try{var bytes=read(file,size);if(bytes is null)return unchecked((int)0x80070490);png=Marshal.AllocCoTaskMem(bytes.Length);Marshal.Copy(bytes,0,png,bytes.Length);length=(uint)bytes.Length;return 0;}catch{if(png!=0)Marshal.FreeCoTaskMem(png);png=0;length=0;return unchecked((int)0x80004005);}};
            var run=Marshal.GetDelegateForFunctionPointer<Run>(NativeLibrary.GetExport(_library,"HybridMountRunThumbnailServer"));var thread=new Thread(()=>{try{_exit.SetResult(run(_read,0,_stop.SafeWaitHandle.DangerousGetHandle(),idle));}catch(Exception error){_exit.SetException(error);}}){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        }
        internal Provider Create(string path)=>new(path,_exit.Task);
        internal void Stop()=>_stop.Set();
        public void Dispose(){_stop.Set();Assert.True(_exit.Task.Wait(TimeSpan.FromSeconds(10)),"COM对象必须释放后再停止宿主");Assert.Equal(0,_exit.Task.GetAwaiter().GetResult());GC.KeepAlive(_read);_stop.Dispose();NativeLibrary.Free(_library);Registry.CurrentUser.DeleteSubKeyTree(@"Software\HybridMount.NET.Tests\ShellProbe\d0037037",false);}
    }
    private sealed unsafe class Provider:IDisposable
    {
        private nint _initialize,_thumbnail;
        internal Provider(string path,Task<int> exit)
        {
            var clsid=Host.ClassId;var iid=new Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841");var deadline=DateTime.UtcNow.AddSeconds(10);int status;
            while((status=CoCreateInstance(in clsid,0,4,in iid,out _initialize))<0){if(DateTime.UtcNow>=deadline||exit.IsCompleted)Marshal.ThrowExceptionForHR(status);Thread.Sleep(10);}
            var item=Marshal.GetComInterfaceForObject<IdentityShellItem,IIdentityShellItem>(new(path));
            try{Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,nint,uint,int>)Table(_initialize)[3])(_initialize,item,0));}finally{Release(item);}
            var thumbnailId=new Guid("E357FCCD-A995-4576-B01F-234630154E96");nint thumbnail=0;Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Table(_initialize)[0])(_initialize,&thumbnailId,&thumbnail));_thumbnail=thumbnail;
        }
        internal int Thumbnail(uint size,out uint alpha,out nint bitmap){uint kind=0;nint image=0;var status=((delegate* unmanaged[Stdcall]<nint,uint,nint*,uint*,int>)Table(_thumbnail)[3])(_thumbnail,size,&image,&kind);alpha=kind;bitmap=image;return status;}
        private static void** Table(nint value)=>*(void***)value;
        private static void Release(nint value){if(value!=0)((delegate* unmanaged[Stdcall]<nint,uint>)Table(value)[2])(value);}
        public void Dispose(){Release(_thumbnail);Release(_initialize);_thumbnail=0;_initialize=0;}
        [DllImport("ole32.dll")]private static extern int CoCreateInstance(in Guid clsid,nint outer,uint context,in Guid iid,out nint result);
    }
}
