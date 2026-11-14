using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace HybridMount.Models.Thumbnails;

/// <summary>无启动反馈的预览子进程；只允许三个匿名管道继承，挂起创建后由调用者先加入受限Job再恢复。流及进程仅属于本次调用。</summary>
internal sealed class BackgroundPreviewProcess : IDisposable
{
    public Process Process {get;}
    public Stream Input {get;}
    public Stream Output {get;}
    public Stream Error {get;}
    private readonly SafeFileHandle _thread;
    private readonly string _executable, _role;
    private int _disposed;
    private BackgroundPreviewProcess(Process process,SafeFileHandle thread,Stream input,Stream output,Stream error,string executable,string role)
    {Process=process;_thread=thread;Input=input;Output=output;Error=error;_executable=executable;_role=role;}
    /// <summary>仅启动部署内已指定的解码角色，路径不经Shell解释；任何创建失败终止本次子进程并归还全部句柄。</summary>
    public static BackgroundPreviewProcess Start(string executable,params string[] arguments)
    {
        executable=Path.GetFullPath(executable);if(executable.Contains('"'))throw new ArgumentException("工作进程路径无效。");
        if(arguments.Length==0)arguments=["--preview-worker"];
        if(arguments.Any(value=>value.Length>128||value.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c=='-'))))throw new ArgumentException("原生角色参数无效。");
        var command="\""+executable+"\" "+string.Join(" ",arguments);
        var packaged=HybridMount.Services.Runtime.NativeProcessIdentity.IsPackaged;
        var security=new SecurityAttributes{Size=Marshal.SizeOf<SecurityAttributes>(),Inherit=1};
        SafeFileHandle? inputRead=null,inputWrite=null,outputRead=null,outputWrite=null,errorRead=null,errorWrite=null,native=null,thread=null;
        Process? process=null;nint attributes=0,handles=0,policy=0;var initialized=false;var transferred=false;
        try
        {
            Check(CreatePipe(out inputRead,out inputWrite,ref security,0));Check(CreatePipe(out outputRead,out outputWrite,ref security,0));Check(CreatePipe(out errorRead,out errorWrite,ref security,0));
            Check(SetHandleInformation(inputWrite,1,0));Check(SetHandleInformation(outputRead,1,0));Check(SetHandleInformation(errorRead,1,0));
            nuint length=0;var count=packaged?2:1;InitializeProcThreadAttributeList(0,count,0,ref length);attributes=Marshal.AllocHGlobal(checked((nint)length));Check(InitializeProcThreadAttributeList(attributes,count,0,ref length));initialized=true;
            handles=Marshal.AllocHGlobal(3*nint.Size);Marshal.WriteIntPtr(handles,inputRead.DangerousGetHandle());Marshal.WriteIntPtr(handles,nint.Size,outputWrite.DangerousGetHandle());Marshal.WriteIntPtr(handles,2*nint.Size,errorWrite.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes,0,0x20002,handles,(nuint)(3*nint.Size),0,0));
            if(packaged)
            {
                // WinBase.h: OVERRIDE=4 | DISABLE_PROCESS_TREE=2；保留本次子进程及后代的包运行环境。
                policy=Marshal.AllocHGlobal(sizeof(uint));Marshal.WriteInt32(policy,6);
                Check(UpdateProcThreadAttribute(attributes,0,0x20012,policy,sizeof(uint),0,0));
            }
            var startup=new StartupInfoEx{Info=new StartupInfo{Size=Marshal.SizeOf<StartupInfoEx>(),Flags=0x100|0x80,Input=inputRead.DangerousGetHandle(),Output=outputWrite.DangerousGetHandle(),Error=errorWrite.DangerousGetHandle()},Attributes=attributes};
            Check(CreateProcess(executable,new StringBuilder(command),0,0,true,0x08000000|0x4|0x80000,0,Path.GetDirectoryName(executable)!,ref startup,out var info));
            native=new(info.Process,true);thread=new(info.Thread,true);process=Process.GetProcessById(info.ProcessId);_ = process.Handle;
            var child=new BackgroundPreviewProcess(process,thread,new FileStream(inputWrite,FileAccess.Write),new FileStream(outputRead,FileAccess.Read),new FileStream(errorRead,FileAccess.Read),executable,arguments[0]=="--preview-worker"?"PreviewWorker":"NativeRuntime");
            transferred=true;inputWrite=null;outputRead=null;errorRead=null;process=null;thread=null;return child;
        }
        finally
        {
            if(!transferred&&native is {IsInvalid:false})TerminateProcess(native,1);
            process?.Dispose();thread?.Dispose();native?.Dispose();inputRead?.Dispose();inputWrite?.Dispose();outputRead?.Dispose();outputWrite?.Dispose();errorRead?.Dispose();errorWrite?.Dispose();
            if(initialized)DeleteProcThreadAttributeList(attributes);if(attributes!=0)Marshal.FreeHGlobal(attributes);if(handles!=0)Marshal.FreeHGlobal(handles);if(policy!=0)Marshal.FreeHGlobal(policy);
        }
    }
    /// <summary>调用者已加入Job后恢复唯一主线程；失败由调用者统一终止，不影响其他进程。</summary>
    public void Resume(){HybridMount.Services.Runtime.NativeProcessIdentity.Verify(Process,_executable,_role);if(ResumeThread(_thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());}
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        try{if(!Process.HasExited)Process.Kill();}catch(InvalidOperationException){}catch(Win32Exception){}
        finally{_thread.Dispose();Input.Dispose();Output.Dispose();Error.Dispose();Process.Dispose();}
    }
    private static void Check(bool value){if(!value)throw new Win32Exception(Marshal.GetLastWin32Error());}
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes{public int Size;public nint Descriptor;public int Inherit;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo{public int Size;public nint Reserved,Desktop,Title;public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;public ushort Show,ReservedSize;public nint ReservedBytes,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)]private struct StartupInfoEx{public StartupInfo Info;public nint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInfo{public nint Process,Thread;public int ProcessId,ThreadId;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref SecurityAttributes security,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool InitializeProcThreadAttributeList(nint attributes,int count,int flags,ref nuint bytes);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool UpdateProcThreadAttribute(nint attributes,uint flags,nuint attribute,nint value,nuint size,nint previous,nint returned);
    [DllImport("kernel32.dll")]private static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateProcess(string application,StringBuilder command,nint processSecurity,nint threadSecurity,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,nint environment,string directory,ref StartupInfoEx startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool TerminateProcess(SafeFileHandle process,uint code);
}
