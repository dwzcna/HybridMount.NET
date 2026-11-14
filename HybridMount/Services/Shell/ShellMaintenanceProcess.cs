using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Shell;

internal sealed record ShellMaintenanceRequest(string Nonce,string Operation,OrphanedMountCandidate? Root=null,RecoveryShellProof? Proof=null, ExplorerEntryCandidate? Entry=null, ExplorerEntryProof? EntryProof=null);
internal sealed record ShellMaintenanceReply(RecoveryRegistration[]? Roots=null,RecoveryShellProof? Proof=null,string[]? Remaining=null,string? Error=null,int HResult=0, ExplorerEntryCandidate[]? Entries=null);
/// <summary>一次一进程的私有维护协议。仅接受同映像、同SID及会话的对端，128位随机标识和有界消息；不使用公共IPC。</summary>
internal static class ShellMaintenanceProcess
{
    internal const int MaximumBytes=1024*1024;
    /// <summary>隐藏应用入口即使没有参数也必须退出维护分支，不能落入普通WinUI启动。</summary>
    internal static bool IsMaintenanceEntry()
    {
        uint count=0;var result=GetCurrentApplicationUserModelId(ref count,null);if(result==15703)return false;
        if(result!=122)return false;var name=new System.Text.StringBuilder((int)count);return GetCurrentApplicationUserModelId(ref count,name)==0&&name.ToString().EndsWith("!ShellMaintenance",StringComparison.Ordinal);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern int GetCurrentApplicationUserModelId(ref uint count,System.Text.StringBuilder? name);
    private static string PipeName(string nonce)=>"HybridMount.ShellMaintenance."+nonce;
    /// <summary>后台调用；已打包时激活经隔离验证的win32App入口。独立进程退出或超时均为失败，不能解释为注册已清除。</summary>
    internal static ShellMaintenanceReply Send(string operation,OrphanedMountCandidate? root=null,RecoveryShellProof? proof=null)
        =>SendAsync(operation,root,proof).GetAwaiter().GetResult();
    internal static ShellMaintenanceReply SendExplorer(string operation, ExplorerEntryCandidate? entry=null, ExplorerEntryProof? proof=null)
        =>SendAsync(operation,null,null,entry,proof).GetAwaiter().GetResult();
    private static async Task<ShellMaintenanceReply> SendAsync(string operation,OrphanedMountCandidate? root,RecoveryShellProof? proof,
        ExplorerEntryCandidate? entry=null, ExplorerEntryProof? entryProof=null)
    {
        var nonce=Guid.NewGuid().ToString("N");using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var token=deadline.Token;
        using var pipe=new NamedPipeServerStream(PipeName(nonce),PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var arguments="--shell-maintenance "+Environment.ProcessId+" "+nonce;
        using var child=Start(arguments);
        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid)||pid!=child.Id)throw new IOException("维护通道客户端身份不匹配。");
        ValidatePeer(pid);
        await WriteAsync(pipe,new ShellMaintenanceRequest(nonce,operation,root,proof,entry,entryProof),token).ConfigureAwait(false);
        var reply=await ReadAsync<ShellMaintenanceReply>(pipe,token).ConfigureAwait(false);
        await child.WaitForExitAsync(token).ConfigureAwait(false);
        if(child.ExitCode!=0)throw new IOException("维护入口未正常完成。");
        if(reply.Error is {} error)throw new ShellMaintenanceException(error,reply.HResult);
        return reply;
    }
    /// <summary>无UI入口；只分发固定操作，任何参数、对端或消息校验失败都不执行注册写入。</summary>
    internal static int Run(string[] args)
    {
        if(args is not ["--shell-maintenance",var parent,var nonce]||!uint.TryParse(parent,out var parentId)||!Guid.TryParseExact(nonce,"N",out _))return 2;
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));var token=deadline.Token;
            ValidatePeer(parentId);
            using var pipe=new NamedPipeClientStream(".",PipeName(nonce),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            pipe.ConnectAsync(token).GetAwaiter().GetResult();
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var serverId)||serverId!=parentId)throw new IOException("维护通道服务端身份不匹配。");
            var request=ReadAsync<ShellMaintenanceRequest>(pipe,token).GetAwaiter().GetResult();
            if(request.Nonce!=nonce)throw new IOException("维护请求标识无效。");
            ShellMaintenanceReply reply;
            try{reply=Dispatch(request);}
            catch(Exception error){reply=new(Error:error.Message,HResult:error is System.ComponentModel.Win32Exception native?unchecked((int)(0x80070000u|(uint)native.NativeErrorCode)):error.HResult);}
            WriteAsync(pipe,reply,token).GetAwaiter().GetResult();return 0;
        }
        catch{return 3;}
    }
    internal static ShellMaintenanceReply Dispatch(ShellMaintenanceRequest request)
    {
        // 维护进程不调用WinRT枚举或注销、不构造应用服务，仅访问白名单注册位置。
        if (request.Operation.StartsWith("explorer", StringComparison.Ordinal)) return DispatchExplorer(request);
        if (request.Entry is not null || request.EntryProof is not null) throw new IOException("维护参数不匹配。");
        var shell=new RecoveryShellRegistration(includeWinRt:false);
        return request.Operation switch
        {
            "discover" when request.Root is null&&request.Proof is null=>new(Roots:shell.List().ToArray()),
            "capture" when request.Root is {} root&&request.Proof is null=>new(Proof:shell.Capture(root)),
            "cleanup" when request.Root is {} root&&request.Proof is {} proof=>Cleanup(shell,root,proof),
            "verify" when request.Root is {} root=>new(Remaining:shell.Remaining(root,request.Proof)),
            _=>throw new IOException("维护操作或参数无效。")
        };
    }
    /// <summary>跨模式仅限精确导航注册；不构造业务服务、不注销 CFAPI 根或遍历目录内容。</summary>
    private static ShellMaintenanceReply DispatchExplorer(ShellMaintenanceRequest request)
    {
        if (request.Root is not null || request.Proof is not null) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError46"));
        var shell = new ExplorerEntryRegistration(new ExplorerEntryRegistry(), WindowsIdentity.GetCurrent().User!.Value);
        if (request.Operation == "explorerDiscover" && request.Entry is null && request.EntryProof is null)
            return new(Entries: shell.Discover(default));
        if (request.Entry is not {} entry || request.EntryProof is not {} proof) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError47"));
        if (request.Operation == "explorerVerify") return new(Remaining: shell.Remaining(entry, proof));
        if (request.Operation != "explorerRemove") throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError48"));
        var guard = new ExplorerEntryPathGuard();
        void Validate()
        {
            ExplorerEntryRecovery.VerifyManaged(entry, ExplorerEntryRecovery.ReadPhysicalDefinitions());
            if (entry.LocalPath is null || guard.Inspect(entry.LocalPath) != entry.Evidence) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError49"));
        }
        shell.Remove(entry, proof, Validate, default);
        return new(Remaining: shell.Remaining(entry, proof));
    }
    private static ShellMaintenanceReply Cleanup(RecoveryShellRegistration shell,OrphanedMountCandidate root,RecoveryShellProof proof)
    {
        void Verify()
        {
            MountRemovalCompletion.VerifyLocal(root);
            if(LocalFileMetadata.TryRead(root.LocalPath) is not null&&CloudFilesNative.RootIdentity(root.LocalPath,true) is not null)throw new IOException("原生根仍存在，拒绝清理注册。");
            if(root.ContentStamp is {} stamp&&MountRemovalCompletion.ContentStamp(root,stamp=="empty"?MountRemovalMode.ClearContents:MountRemovalMode.PreserveFiles)!=stamp)throw new IOException("内容证据已变化，拒绝Shell清理。");
        }
        if(root.ContentStamp is null)throw new IOException("缺少内容完成证据，拒绝Shell清理。");
        Verify();
        shell.Cleanup(root,proof,Verify);return new(Remaining:shell.Remaining(root,proof));
    }
    private static Process Start(string arguments)
    {
        if(!WinRegisterService.IsPackaged())
            return Process.Start(new ProcessStartInfo(Environment.ProcessPath!){Arguments=arguments,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})??throw new IOException("不能启动维护进程。");
        var activation=(IActivation)new ActivationManager();
        try
        {
            Marshal.ThrowExceptionForHR(activation.ActivateApplication(global::Windows.ApplicationModel.Package.Current.Id.FamilyName+"!ShellMaintenance",arguments,0,out var pid));
            var child=Process.GetProcessById((int)pid);
            try
            {
                // 系统激活的进程不是Process.Start的子进程，必须在其退出前保留句柄，才能可靠等待和读取退出码。
                _=child.SafeHandle;return child;
            }
            catch{child.Dispose();throw;}
        }
        finally{Marshal.ReleaseComObject(activation);}
    }
    /// <summary>用持有的进程句柄读取令牌；不同SID、会话或可执行文件均拒绝。维护端不提升权限。</summary>
    private static void ValidatePeer(uint pid)
    {
        using var peer=Process.GetProcessById(checked((int)pid));using var self=Process.GetCurrentProcess();
        if(peer.SessionId!=self.SessionId||!string.Equals(peer.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("维护对端映像或会话不匹配。");
        if(!OpenProcessToken(peer.Handle,8,out var handle))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using(handle)using(var identity=new WindowsIdentity(handle.DangerousGetHandle()))
        {if(identity.User!=WindowsIdentity.GetCurrent().User)throw new IOException("维护对端不属于当前用户。");}
    }
    internal static async Task WriteAsync<T>(Stream stream,T value,CancellationToken token)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length>MaximumBytes)throw new IOException("维护消息过大。");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length),token).ConfigureAwait(false);await stream.WriteAsync(bytes,token).ConfigureAwait(false);await stream.FlushAsync(token).ConfigureAwait(false);
    }
    internal static async Task<T> ReadAsync<T>(Stream stream,CancellationToken token)
    {
        var prefix=new byte[4];await stream.ReadExactlyAsync(prefix,token).ConfigureAwait(false);var size=BitConverter.ToInt32(prefix);
        if(size is <2 or >MaximumBytes)throw new IOException("维护消息长度无效。");var bytes=new byte[size];await stream.ReadExactlyAsync(bytes,token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes,new JsonSerializerOptions{MaxDepth=32})??throw new IOException("维护消息为空。");
    }
    [ComImport,Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]private class ActivationManager{}
    [ComImport,Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IActivation
    {
        [PreserveSig]int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string app,[MarshalAs(UnmanagedType.LPWStr)]string args,uint flags,out uint pid);
        [PreserveSig]int ActivateForFile(nint items,[MarshalAs(UnmanagedType.LPWStr)]string verb,out uint pid);
        [PreserveSig]int ActivateForProtocol(nint items,out uint pid);
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenProcessToken(nint process,uint access,out SafeAccessTokenHandle token);
}
/// <summary>保留维护失败的原始HRESULT供恢复记录和诊断，不改变上传或文件内容状态。</summary>
internal sealed class ShellMaintenanceException:IOException
{internal ShellMaintenanceException(string message,int result):base(message){HResult=result;}}
