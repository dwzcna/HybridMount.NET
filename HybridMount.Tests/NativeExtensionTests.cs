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
        private static readonly Guid CommandId = new("F4A8FD0B-4055-4F23-8CC0-91B736B3E491");
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
                keep = Next(clone); Assert.Equal("始终保留在设备上", Title(keep));
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
        public void Dispose()
        {
            Release(_root); Release(_factory); _root = _factory = 0;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(_library,"DllCanUnloadNow"))());
            NativeLibrary.Free(_library);
        }
        [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint context, in Guid iid, out nint item);
        [DllImport("shell32.dll")] private static extern int SHCreateShellItemArrayFromShellItem(nint item, in Guid iid, out nint array);
    }
    /// <summary>三个原生命令正确发送中文、引号和反斜杠，选择的文件内容不变。</summary>
    [Fact]
    public async Task NativeComAndPipeRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HybridMount.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "原生测试.txt"); await File.WriteAllTextAsync(file,"original");
        try
        {
            await using var logger = new LoggerService(Path.Combine(directory,"log.db"));
            await using var server = new IpcServerService(logger,NativePreviewInterop.PipeName);
            var received = System.Threading.Channels.Channel.CreateUnbounded<IpcRequest>();
            server.RequestReceived += request => received.Writer.TryWrite(request);
            server.Start();
            using var extension = new NativeExtension();
            extension.VerifyEnumeration();
            foreach (var keep in new[] { false, true })
            {
                extension.Invoke(file, keep);
                var request = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal(keep ? "keepOnDevice" : "freeSpace",request.Operation);
                Assert.Equal(file, Assert.Single(request.Paths));
            }
            extension.Invoke(file,false,true);var issue=await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));Assert.Equal("showSyncIssues",issue.Operation);Assert.Equal(file,Assert.Single(issue.Paths));
            Assert.Equal("original", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(directory,true); }
    }
}
