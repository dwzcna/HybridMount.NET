using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Models.Mounting.Recovery;
namespace HybridMount.Tests;

/// <summary>仅操作随机NTFS临时目录和独立原生进程；不创建Shell入口、不连接远端、不触碰正式挂载。</summary>
[Collection("Native IPC")]
public sealed class OrphanedDirectoryRecoveryTests
{
    [Fact(Timeout=60000)]
    public async Task MissingRootWithColdPlaceholderClearsThroughConnectedTemporaryProvider()
    {
        await using var area=await Area.Create();
        var root=await area.Service().InspectAsync(area.Local,default);
        Assert.True(root.OrphanedDirectory);Assert.False(root.Registered);Assert.Equal("",root.SyncRootId);
        var before=MountPathGuard.FileIdentity(area.Local);var stages=new List<string>();
        var result=await area.Service().ClearAsync(root,default,stages.Add);
        Assert.True(result.Completed,string.Join(";",result.Errors));Assert.True(Directory.Exists(area.Local));Assert.Empty(Directory.EnumerateFileSystemEntries(area.Local));
        Assert.Equal(before,MountPathGuard.FileIdentity(area.Local));Assert.True((await area.Runtime.RootInfoAsync(area.Local)).GetProperty("hresult").GetInt32()<0);
        Assert.Contains(stages,x=>x.Contains("已连接",StringComparison.Ordinal));Assert.Empty(await area.Service().DiscoverAsync(default));
        using var process=Process.GetProcessById(area.Runtime.ProcessId);Assert.DoesNotContain(process.Modules.Cast<ProcessModule>(),x=>string.Equals(x.ModuleName,"coreclr.dll",StringComparison.OrdinalIgnoreCase));
    }
    [Fact(Timeout=60000)]
    public async Task LockedFileFailsThenExplicitRetryUsesSameJournalWithoutRemoteSource()
    {
        await using var area=await Area.Create();var service=area.Service();var root=await service.InspectAsync(area.Local,default);
        using(var locked=CloudFilesNative.CreateFile(Path.Combine(area.Local,"local.txt"),0x80,1,0,3,0x02300000,0))
        {
            Assert.False(locked.IsInvalid);var failed=await service.ClearAsync(root,default);
            Assert.False(failed.Completed);Assert.NotNull(failed.HResult);Assert.True(File.Exists(Path.Combine(area.Local,"local.txt")));
        }
        var pending=Assert.Single(await service.DiscoverAsync(default));Assert.Equal(root.MountId,pending.MountId);
        Assert.True((await service.ClearAsync(pending,default)).Completed);Assert.Empty(await service.DiscoverAsync(default));
    }
    [Fact(Timeout=60000)]
    public async Task CancelAfterRegistrationCleansTemporaryRootAndRequiresExplicitRetry()
    {
        await using var area=await Area.Create();var service=area.Service();var root=await service.InspectAsync(area.Local,default);using var cancel=new CancellationTokenSource();var stages=new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.ClearAsync(root,cancel.Token,line=>{stages.Add(line);if(line.Contains("临时注册已核验",StringComparison.Ordinal))cancel.Cancel();}));
        var native=await area.Runtime.RootInfoAsync(area.Local);Assert.True(native.GetProperty("hresult").GetInt32()<0,native.GetRawText());
        // 系统注销可能删除无法再使用的冷占位项；普通本地文件必须保持，不能假设冷项仍可打开。
        Assert.Equal("local data",await File.ReadAllTextAsync(Path.Combine(area.Local,"local.txt")));
        Assert.DoesNotContain(stages,line=>line.Contains("已连接",StringComparison.Ordinal)||line.Contains("正在清空",StringComparison.Ordinal));
        var pending=Assert.Single(await service.DiscoverAsync(default));Assert.True((await service.ClearAsync(pending,default)).Completed);
    }
    [Fact(Timeout=60000)]
    public async Task ProcessCrashLeavesDiscoverableIntentAndExplicitRetryCompletes()
    {
        await using var area=await Area.Create();var service=area.Service();var root=await service.InspectAsync(area.Local,default);var killed=false;
        await Assert.ThrowsAnyAsync<Exception>(()=>service.ClearAsync(root,default,line=>
        {
            if(killed||!line.Contains("已连接",StringComparison.Ordinal))return;killed=true;
            using var process=Process.GetProcessById(area.Runtime.ProcessId);process.Kill();process.WaitForExit(5000);
        }));
        Assert.True(killed);await area.RestartRuntime();
        var pending=Assert.Single(await area.Service().DiscoverAsync(default));Assert.Equal(root.MountId,pending.MountId);
        Assert.True((await area.Service().ClearAsync(pending,default)).Completed);
    }
    [Fact(Timeout=60000)]
    public async Task PlainDirectoryAndStaleSnapshotCannotStartTemporaryRegistration()
    {
        await using var area=await Area.Create();var ordinary=Path.Combine(area.Directory,"ordinary");System.IO.Directory.CreateDirectory(ordinary);await File.WriteAllTextAsync(Path.Combine(ordinary,"keep.txt"),"keep");
        await Assert.ThrowsAnyAsync<Exception>(()=>area.Service().InspectAsync(ordinary,default));
        Assert.Equal("keep",await File.ReadAllTextAsync(Path.Combine(ordinary,"keep.txt")));
        var root=await area.Service().InspectAsync(area.Local,default);
        await Assert.ThrowsAsync<IOException>(()=>area.Service().ClearAsync(root with{RootIdentity="changed"},default));
        Assert.True(OnDiskExists(area.Cold));Assert.False(System.IO.Directory.Exists(area.Journals));
    }
    [Fact(Timeout=60000)]
    public async Task ExternalJunctionIsUnlinkedWithoutTouchingItsTarget()
    {
        await using var area=await Area.Create();var outside=Path.Combine(area.Directory,"outside");System.IO.Directory.CreateDirectory(outside);var sentinel=Path.Combine(outside,"keep.txt");await File.WriteAllTextAsync(sentinel,"unchanged");
        var link=Path.Combine(area.Local,"external");System.IO.Directory.CreateSymbolicLink(link,outside);
        var root=await area.Service().InspectAsync(area.Local,default);Assert.True((await area.Service().ClearAsync(root,default)).Completed);
        Assert.Equal("unchanged",await File.ReadAllTextAsync(sentinel));
    }
    [Fact(Timeout=60000)]
    public async Task ManagedAndNestedRegisteredDirectoriesAreRejected()
    {
        await using var area=await Area.Create();var definition=new MountDefinition(Guid.NewGuid(),"managed",area.Local,false,RootIdentity:MountPathGuard.FileIdentity(area.Local));
        var protectedService=new OrphanedDirectoryRecovery(area.Journals,()=>[definition],runtime:area.Runtime);
        await Assert.ThrowsAsync<IOException>(()=>protectedService.InspectAsync(area.Local,default));
        var nested=Path.Combine(area.Local,"registered");System.IO.Directory.CreateDirectory(nested);var identity=Encoding.UTF8.GetBytes("nested-"+Guid.NewGuid());
        Area.Register(nested,identity);
        try{await Assert.ThrowsAnyAsync<Exception>(()=>area.Service().InspectAsync(area.Local,default));Assert.True(OnDiskExists(area.Cold));}
        finally{CloudFilesNative.Check(Unregister(nested));}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Registration
    {
        public uint Size;[MarshalAs(UnmanagedType.LPWStr)]public string Name;[MarshalAs(UnmanagedType.LPWStr)]public string Version;
        public nint Identity;public uint IdentityLength;public nint FileIdentity;public uint FileIdentityLength;public Guid ProviderId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Policies {public uint Size,Hydration,Population,InSync,Hardlink,PlaceholderManagement;}
    [DllImport("cldapi.dll",EntryPoint="CfRegisterSyncRoot",CharSet=CharSet.Unicode)]
    private static extern int RegisterNative(string path,in Registration registration,in Policies policies,uint flags);
    /// <summary>注销后的冷占位符可能被兼容层隐藏；只用属性句柄核验磁盘条目，不尝试打开正文。</summary>
    private static bool OnDiskExists(string path)
    {
        var before=RtlSetThreadPlaceholderCompatibilityMode(2);Assert.True(before>=0);
        try{using var file=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02300000,0);Assert.True(!file.IsInvalid,$"磁盘属性核验失败：{Marshal.GetLastWin32Error()}");return true;}
        finally{RtlSetThreadPlaceholderCompatibilityMode(before);}
    }
    [DllImport("ntdll.dll")]
    private static extern sbyte RtlSetThreadPlaceholderCompatibilityMode(sbyte mode);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file,uint control,nint input,uint inputSize,byte[] output,uint outputSize,out uint returned,nint overlapped);
    [DllImport("advapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle,uint information,byte[] descriptor);
    [DllImport("cldapi.dll",EntryPoint="CfUnregisterSyncRoot",CharSet=CharSet.Unicode)]
    private static extern int Unregister(string path);
    private sealed class Area:IAsyncDisposable
    {
        internal readonly string Directory=Path.Combine(Path.GetTempPath(),"HybridMount.Orphan60."+Guid.NewGuid().ToString("N"));
        internal string Local=>Path.Combine(Directory,"view");internal string Cold=>Path.Combine(Local,"cold.bin");internal string Journals=>Path.Combine(Directory,"journals");
        internal NativeRuntimeClient Runtime=null!;private byte[] _initial=[];
        internal OrphanedDirectoryRecovery Service()=>new(Journals,()=>[],runtime:Runtime);
        internal static void Register(string path,byte[] identity)
        {
            var memory=Marshal.AllocHGlobal(identity.Length);
            try
            {
                Marshal.Copy(identity,0,memory,identity.Length);var registration=new Registration{Size=(uint)Marshal.SizeOf<Registration>(),Name="HybridMount isolated orphan test",Version="1",Identity=memory,IdentityLength=(uint)identity.Length,ProviderId=Guid.NewGuid()};
                var policies=new Policies{Size=(uint)Marshal.SizeOf<Policies>(),Hydration=0,Population=3};
                CloudFilesNative.Check(RegisterNative(path,registration,policies,0));
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
        internal static async Task<Area> Create()
        {
            var area=new Area();System.IO.Directory.CreateDirectory(area.Local);area.Runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
            area._initial=Encoding.UTF8.GetBytes("orphan-test-"+Guid.NewGuid());Register(area.Local,area._initial);
            try
            {
                var identity=Encoding.UTF8.GetBytes("cold-no-provider");var memory=Marshal.AllocHGlobal(identity.Length);
                try
                {
                    Marshal.Copy(identity,0,memory,identity.Length);
                    var files=new[]{new CloudFilesNative.PlaceholderCreateInfo{RelativeName="cold.bin",Metadata=new(){Basic=new(){FileAttributes=0x20},FileSize=1048576},Identity=memory,IdentityLength=(uint)identity.Length,Flags=0}};
                    CloudFilesNative.Check(CloudFilesNative.CfCreatePlaceholders(area.Local,files,1,0,out var count));Assert.Equal(1u,count);CloudFilesNative.Check(files[0].Result);
                }
                finally{Marshal.FreeHGlobal(memory);}
                // 占住文件属性句柄，使注销保留无法访问的冷占位符，复现缺失原生根的现场。
                var fileInfo=new FileInfo(area.Cold);var directoryInfo=new DirectoryInfo(area.Local);
                var originalFile=fileInfo.GetAccessControl();var originalDirectory=directoryInfo.GetAccessControl();
                try
                {
                    var sid=WindowsIdentity.GetCurrent().User!;
                    var fileSecurity=fileInfo.GetAccessControl();fileSecurity.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.Delete|FileSystemRights.WriteAttributes|FileSystemRights.WriteData,AccessControlType.Deny));
                    var directorySecurity=directoryInfo.GetAccessControl();directorySecurity.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.DeleteSubdirectoriesAndFiles,AccessControlType.Deny));
                    fileInfo.SetAccessControl(fileSecurity);directoryInfo.SetAccessControl(directorySecurity);
                    var code=Unregister(area.Local);Trace.WriteLine($"Isolated orphan seed unregistration: 0x{code:X8}");
                }
                finally
                {
                    using(var restoreRoot=CloudFilesNative.CreateFile(area.Local,0x40000,7,0,3,0x02300000,0))
                    {Assert.False(restoreRoot.IsInvalid);if(!SetKernelObjectSecurity(restoreRoot,4,originalDirectory.GetSecurityDescriptorBinaryForm()))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
                    using var restore=CloudFilesNative.CreateFile(area.Cold,0x40000,7,0,3,0x02300000,0);
                    if(!restore.IsInvalid){if(!SetKernelObjectSecurity(restore,4,originalFile.GetSecurityDescriptorBinaryForm()))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
                    else if(Marshal.GetLastWin32Error()!=2)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                var root=await area.Runtime.RootInfoAsync(area.Local);Assert.True(root.GetProperty("hresult").GetInt32()<0);
                // 仅同步核验所在的当前线程暴露占位符；不能把线程兼容模式跨越await。
                var before=RtlSetThreadPlaceholderCompatibilityMode(2);Assert.True(before>=0);
                try
                {
                    using var raw=CloudFilesNative.CreateFile(area.Cold,0x80,7,0,3,0x02300000,0);
                    Assert.False(raw.IsInvalid);var bytes=new byte[16384];var queried=DeviceIoControl(raw,0x900a8,0,0,bytes,(uint)bytes.Length,out var returned,0);
                    Assert.True(queried,$"FSCTL_GET_REPARSE_POINT failed: {Marshal.GetLastWin32Error()} at {area.Cold}");Assert.True(returned>=8);
                    var tag=BitConverter.ToUInt32(bytes);Assert.True(CloudFilesNative.IsCloudTag(tag),$"Expected orphan cloud tag, actual 0x{tag:X8}");
                }
                finally{RtlSetThreadPlaceholderCompatibilityMode(before);}
                await File.WriteAllTextAsync(Path.Combine(area.Local,"local.txt"),"local data");
                return area;
            }
            catch(Exception failure)
            {
                try{await area.DisposeAsync();}catch(Exception cleanup){throw new AggregateException("隔离现场："+area.Directory,failure,cleanup);}throw;
            }
        }
        internal async Task RestartRuntime(){await Runtime.DisposeAsync();Runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();}
        public async ValueTask DisposeAsync()
        {
            try
            {
                if(!Runtime.IsAvailable)await RestartRuntime();
                if(System.IO.Directory.Exists(Local))
                {
                    var root=await Runtime.RootInfoAsync(Local);
                    if(root.GetProperty("hresult").GetInt32()>=0&&root.GetProperty("identity").GetBytesFromBase64().SequenceEqual(_initial))CloudFilesNative.Check(Unregister(Local));
                    try{var candidate=await Service().InspectAsync(Local,default);var result=await Service().ClearAsync(candidate,default);if(!result.Completed)throw new IOException(result.Stage+": "+string.Join(";",result.Errors));}
                    catch(COMException error)when(error.HResult==unchecked((int)0x80070178)){}
                }
            }
            finally
            {
                await Runtime.DisposeAsync();
                var prefix=Path.Combine(Path.GetTempPath(),"HybridMount.Orphan60.");
                if(!Directory.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("测试路径越界。");
                if(System.IO.Directory.Exists(Directory))System.IO.Directory.Delete(Directory,true);
            }
        }
    }
}
