using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>只启动本产品的独立工作模式；匿名管道传输有界字节，512MiB Job 与十秒期限限制系统解码器。</summary>
public sealed class PreviewWorkerClient(string? executable=null,Action<int>? diagnostic=null)
{
    private static string DefaultExecutable()
    {
#if HYBRIDMOUNT_HEADLESS_TEST
        var sibling=Path.Combine(AppContext.BaseDirectory,"HybridMount.Native.exe");if(File.Exists(sibling))return sibling;
#endif
        return Path.Combine(AppContext.BaseDirectory,"HybridMount.Native.exe");
    }
    /// <summary>一个工作进程生成四个尺寸，共用读取预算；任何失败均由调用者回退图标，finally 只结束本次创建的进程。</summary>
    public async Task<Dictionary<int,byte[]>?> DecodeAsync(string extension,long length,Func<long,int,CancellationToken,Task<byte[]>> read,CancellationToken token=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(10));var stopping=timeout.Token;
        var start=new ProcessStartInfo(Path.GetFullPath(executable??DefaultExecutable())){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--preview-worker");
        using var process=Process.Start(start)??throw new IOException("无法启动预览工作进程。");using var job=CreateJobObject(0,null);
        var limits=new ExtendedLimits{Basic=new BasicLimits{Flags=0x2000|0x100},ProcessMemoryLimit=(nuint)(512L*1024*1024)};
        try
        {
            if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())||!AssignProcessToJobObject(job,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
            var drain=process.StandardError.BaseStream.CopyToAsync(Stream.Null,stopping);
            await IpcProtocol.WriteAsync(process.StandardInput.BaseStream,new PreviewWorkerPacket("decode",Extension:extension.ToLowerInvariant(),Length:length),stopping).ConfigureAwait(false);
            while(true)
            {
                var message=await IpcProtocol.ReadAsync<PreviewWorkerPacket>(process.StandardOutput.BaseStream,stopping).ConfigureAwait(false);
                if(message.Kind=="unsupported"){if(message.HResult is {} code)diagnostic?.Invoke(code);return null;}
                if(message.Kind=="result")
                {
                    var frames=message.Frames;if(frames is null||frames.Count!=4)return null;
                    foreach(var size in PreviewWorker.Sizes)
                    {
                        if(!frames.TryGetValue(size,out var png)||png.Length is <24 or >ExePreviewCache.MaximumPngBytes||!png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))return null;
                        var width=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));var height=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));if(width<1||height<1||width>size||height>size)return null;
                    }
                    await process.WaitForExitAsync(stopping).ConfigureAwait(false);await drain.ConfigureAwait(false);return process.ExitCode==0?frames:null;
                }
                if(message.Kind!="read"||message.Offset<0||message.Count is <1 or >PreviewRangeBudget.WindowBytes||message.Offset>length-message.Count)throw new IOException("工作进程读取越界。");
                var data=await read(message.Offset,message.Count,stopping).ConfigureAwait(false);if(data.Length!=message.Count)throw new IOException("预览读取长度不符。");
                await IpcProtocol.WriteAsync(process.StandardInput.BaseStream,new PreviewWorkerPacket("data",Data:data),stopping).ConfigureAwait(false);
            }
        }
        finally
        {
            // 进程对象来自本次 Start，Job 只包含此子进程，不按名称选取或影响用户实例。
            if(!process.HasExited){try{process.Kill();}catch(InvalidOperationException)when(process.HasExited){}await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public nuint MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcesses;public nuint Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public nuint ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(nint security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimits info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
}
