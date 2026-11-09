using HybridMount.Services.Mounting;
using System.Runtime.InteropServices;

namespace HybridMount.Tests;

/// <summary>加载真实扩展 DLL 验证 COM 枚举、模块释放和原生到托管端的消息往返。</summary>
[Collection("Native IPC")]
public sealed class NativeExtensionTests
{
    private sealed unsafe class NativeExtension : IDisposable
    {
        private readonly nint _library;
        private nint _factory, _root;
        private static readonly Guid CommandId = new("59964FC1-D076-4DC1-9778-01F5833A5D4A");
        internal NativeExtension()
        {
            _library = NativeLibrary.Load(NativePreviewInterop.LibraryPath);
            var create = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(_library, "DllGetClassObject");
            var id = CommandId; var factoryId = new Guid("00000001-0000-0000-C000-000000000046");
            nint factory = 0;
            Assert.Equal(0, create(&id, &factoryId, &factory)); _factory = factory;
            var commandId = new Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9");
            nint root = 0;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint,nint,Guid*,nint*,int>)Table(_factory)[3])(_factory, 0, &commandId, &root)); _root = root;
            Assert.Equal("HybridMount.NET", Title(_root));
        }
        private static void** Table(nint instance) => *(void***)instance;
        private static void Release(nint instance) { if (instance != 0) ((delegate* unmanaged[Stdcall]<nint,uint>)Table(instance)[2])(instance); }
        private static string Title(nint instance)
        {
            nint title = 0;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)Table(instance)[3])(instance, 0, &title));
            try { return Marshal.PtrToStringUni(title)!; } finally { Marshal.FreeCoTaskMem(title); }
        }
        private nint Enumerate()
        {
            nint value = 0;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint,nint*,int>)Table(_root)[10])(_root, &value));
            return value;
        }
        private static nint Next(nint enumerator)
        {
            nint value = 0; uint fetched = 0;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint,uint,nint*,uint*,int>)Table(enumerator)[3])(enumerator, 1, &value, &fetched));
            Assert.Equal(1u, fetched); return value;
        }
        /// <summary>每个枚举器独立从首项开始，Clone 从当前位置继续。</summary>
        internal void VerifyEnumeration()
        {
            var first = Enumerate(); var second = Enumerate(); nint clone = 0, free = 0, freeAgain = 0, keep = 0, issues=0;
            try
            {
                free = Next(first); freeAgain = Next(second);
                Assert.Equal("释放空间", Title(free)); Assert.Equal("释放空间", Title(freeAgain));
                Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint,nint*,int>)Table(first)[6])(first, &clone));
                keep = Next(clone); Assert.Equal("始终保留在此设备上", Title(keep));
                issues=Next(clone);Assert.Equal("查看同步问题",Title(issues));
                uint state = 99;
                Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint,nint,int,uint*,int>)Table(free)[7])(free, 0, 1, &state));
                Assert.NotEqual(0u, state);
            }
            finally { Release(issues);Release(keep); Release(freeAgain); Release(free); Release(clone); Release(second); Release(first); }
        }
        /// <summary>调用真实子命令，仅向生产格式的当前用户管道发消息。</summary>
        internal void Invoke(string path, bool keep,bool issues=false)
        {
            var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            var arrayId = new Guid("B63EA76D-1F85-456F-A19C-48159EFA858B");
            Assert.Equal(0, SHCreateItemFromParsingName(path, 0, in itemId, out var item));
            nint array = 0, command = 0, enumerator = Enumerate();
            try
            {
                Assert.Equal(0, SHCreateShellItemArrayFromShellItem(item, in arrayId, out array));
                command = Next(enumerator);
                if (keep||issues) { Release(command); command = Next(enumerator); }
                if(issues){Release(command);command=Next(enumerator);}
                Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint,nint,nint,int>)Table(command)[8])(command, array, 0));
            }
            finally { Release(command); Release(enumerator); Release(array); Release(item); }
        }
        /// <summary>使用真实Shell选择查询子菜单状态；只读查询不得触发水合或远端请求。</summary>
        internal uint State(string path,bool keep)
        {
            var itemId=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");var arrayId=new Guid("B63EA76D-1F85-456F-A19C-48159EFA858B");
            Assert.Equal(0,SHCreateItemFromParsingName(path,0,in itemId,out var item));nint array=0,command=0,enumerator=Enumerate();
            try{Assert.Equal(0,SHCreateShellItemArrayFromShellItem(item,in arrayId,out array));command=Next(enumerator);if(keep){Release(command);command=Next(enumerator);}uint state=99;Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,nint,int,uint*,int>)Table(command)[7])(command,array,1,&state));return state;}
            finally{Release(command);Release(enumerator);Release(array);Release(item);}
        }
        public void Dispose()
        {
            Release(_root); Release(_factory); _root = _factory = 0;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(_library,"DllCanUnloadNow"))());
            NativeLibrary.Free(_library);
        }
        [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint context, in Guid iid, out nint item);
        [DllImport("shell32.dll")] private static extern int SHCreateShellItemArrayFromShellItem(nint item, in Guid iid, out nint array);
    }
    /// <summary>暂缓原生EXCLUDED的冷文件仍隐藏两项菜单，不水合，不访问远端。</summary>
    [Fact(Timeout=60000)]
    public async Task DeferredColdExclusionHidesBothNativeExtensionCommands()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);
        var path=Path.Combine(test.Local,"文件.bin");await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Suffix,".bin")]);
        Assert.NotEqual(3u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(1u,CloudShellState.Read(path).ProviderState);
        var requests=test.Dav.Server.Requests.Count;using var extension=new NativeExtension();
        Assert.NotEqual(0u,extension.State(path,true));Assert.NotEqual(0u,extension.State(path,false));
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(0,CloudFilesNative.Inspect(path)!.Value.Info.OnDiskSize);
    }
    /// <summary>三个原生命令正确发送中文、引号和反斜杠，选择的文件内容不变。</summary>
    [Fact]
    public async Task NativeComAndPipeRoundTrip()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);
        var file=Path.Combine(test.Local,"文件.bin");
        await using var server=new IpcServerService(test.Logger,NativePreviewInterop.PipeName);
        var received=System.Threading.Channels.Channel.CreateUnbounded<IpcRequest>();server.RequestReceived+=request=>received.Writer.TryWrite(request);server.Start();
        using var extension=new NativeExtension();extension.VerifyEnumeration();
        var before=CloudFilesNative.Inspect(file)!.Value;var requests=test.Dav.Server.Requests.Count;
        Assert.Equal(0u,extension.State(file,true));Assert.NotEqual(0u,extension.State(file,false));
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(before.Info.OnDiskSize,CloudFilesNative.Inspect(file)!.Value.Info.OnDiskSize);
        extension.Invoke(file,true);var keep=await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));Assert.Equal("keepOnDevice",keep.Operation);Assert.Equal(file,Assert.Single(keep.Paths));
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[file]))).State);
        var bytes=await File.ReadAllBytesAsync(file);requests=test.Dav.Server.Requests.Count;before=CloudFilesNative.Inspect(file)!.Value;
        Assert.Equal(0u,extension.State(file,false));Assert.NotEqual(0u,extension.State(file,true));
        extension.Invoke(file,false);var free=await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));Assert.Equal("freeSpace",free.Operation);Assert.Equal(file,Assert.Single(free.Paths));
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(before.Info.PinState,CloudFilesNative.Inspect(file)!.Value.Info.PinState);Assert.Equal(bytes,await File.ReadAllBytesAsync(file));
        extension.Invoke(file,false,true);var issue=await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));Assert.Equal("showSyncIssues",issue.Operation);
        using(var handle=CloudFilesNative.CreateFile(file,0x180,7,0,3,0x02200000,0))CloudFilesNative.Check(CloudFilesNative.SetPinIntent(handle,3,0,0));
        Assert.NotEqual(0u,extension.State(file,true));Assert.NotEqual(0u,extension.State(file,false));
    }
}
