using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Thumbnails;

/// <summary>只启动本产品的独立工作模式；匿名管道传输有界字节，512MiB Job 与十秒期限限制系统解码器。</summary>
public sealed class PreviewWorkerClient(string? executable=null,Action<int>? diagnostic=null,Action<string,long>? timing=null)
{
    internal TimeSpan StartupTimeout{get;init;}=TimeSpan.FromSeconds(5);
    internal TimeSpan DecodeTimeout{get;init;}=TimeSpan.FromSeconds(10);
    internal TimeSpan PrefetchTimeout{get;init;}=TimeSpan.FromSeconds(30);
    /// <summary>诊断订阅失败不能跳过子进程及管道的真实收尾。</summary>
    private void RecordTiming(string stage,long milliseconds){try{timing?.Invoke(stage,milliseconds);}catch{/* 仅诊断回调，不能替换业务结果。 */}}
    private static string DefaultExecutable()
    {
#if HYBRIDMOUNT_HEADLESS_TEST
        var sibling=Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe");if(File.Exists(sibling))return sibling;
#endif
        return Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe");
    }
    /// <summary>一个工作进程生成四个尺寸，共用读取预算；任何失败均由调用者回退图标，finally 只结束本次创建的进程。</summary>
    public Task<Dictionary<int,byte[]>?> DecodeAsync(string extension,long length,Func<long,int,CancellationToken,Task<byte[]>> read,CancellationToken token=default)
        =>DecodeCoreAsync(extension,length,async(offset,count,cancel)=>await read(offset,count,cancel).ConfigureAwait(false),token);
    /// <summary>预览不可用由状态结束，不制造正常分支异常；首次预算窗口失败时不启动解码子进程。</summary>
    internal async Task<Dictionary<int,byte[]>?> DecodeAsync(string extension,long length,PreviewRangeBudget budget,CancellationToken token=default)
    {
        var prefetched=Stopwatch.GetTimestamp();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(PrefetchTimeout);
        try{if(length>0&&await budget.TryReadAsync(0,(int)Math.Min(length,PreviewRangeBudget.WindowBytes),deadline.Token).ConfigureAwait(false) is null)return null;}
        catch(OperationCanceledException)when(deadline.IsCancellationRequested&&!token.IsCancellationRequested){throw new PreviewPhaseTimeoutException(PreviewPhase.Prefetch);}
        finally{RecordTiming("首块预取",(long)Stopwatch.GetElapsedTime(prefetched).TotalMilliseconds);}
        token.ThrowIfCancellationRequested();return await DecodeCoreAsync(extension,length,budget.TryReadAsync,token).ConfigureAwait(false);
    }
    private async Task<Dictionary<int,byte[]>?> DecodeCoreAsync(string extension,long length,Func<long,int,CancellationToken,Task<byte[]?>> read,CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(StartupTimeout);var stopping=timeout.Token;var phase=PreviewPhase.Startup;long decoding=0,reading=0;
        var startup=Stopwatch.GetTimestamp();using var child=BackgroundPreviewProcess.Start(executable??DefaultExecutable());var process=child.Process;using var job=CreateJobObject(0,null);Task? drain=null;
        // 匿名同步管道的取消不能只依赖ReadAsync；期限触发仅终止本次拥有的子进程，使在途读真实完成。
        using var terminate=stopping.Register(()=>{try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}catch(Win32Exception){}});
        var limits=new ExtendedLimits{Basic=new BasicLimits{Flags=0x2000|0x100},ProcessMemoryLimit=(nuint)(512L*1024*1024)};
        try
        {
            if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())||!AssignProcessToJobObject(job,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
            child.Resume();drain=child.Error.CopyToAsync(Stream.Null,stopping);
            var ready=await IpcProtocol.ReadAsync<PreviewWorkerPacket>(child.Output,stopping).ConfigureAwait(false);if(ready.Kind!="ready")throw new IOException("预览工作进程未就绪。");
            RecordTiming("启动",(long)Stopwatch.GetElapsedTime(startup).TotalMilliseconds);decoding=Stopwatch.GetTimestamp();phase=PreviewPhase.Decode;timeout.CancelAfter(DecodeTimeout);
            await IpcProtocol.WriteAsync(child.Input,new PreviewWorkerPacket("decode",Extension:extension.ToLowerInvariant(),Length:length),stopping).ConfigureAwait(false);
            while(true)
            {
                var message=await IpcProtocol.ReadAsync<PreviewWorkerPacket>(child.Output,stopping).ConfigureAwait(false);
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
                phase=PreviewPhase.Read;var readStarted=Stopwatch.GetTimestamp();byte[]? data;
                try{data=await read(message.Offset,message.Count,stopping).ConfigureAwait(false);}
                finally{var milliseconds=(long)Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;reading+=milliseconds;RecordTiming("工作读取",milliseconds);}
                phase=PreviewPhase.Decode;if(data is null)return null;if(data.Length!=message.Count)throw new IOException("预览读取长度不符。");
                await IpcProtocol.WriteAsync(child.Input,new PreviewWorkerPacket("data",Data:data),stopping).ConfigureAwait(false);
            }
        }
        catch(Exception)when(stopping.IsCancellationRequested){if(token.IsCancellationRequested)throw new OperationCanceledException(token);throw new PreviewPhaseTimeoutException(phase);}
        finally
        {
            if(decoding!=0){var elapsed=(long)Stopwatch.GetElapsedTime(decoding).TotalMilliseconds;RecordTiming("解码",elapsed);RecordTiming("解码交互",Math.Max(0,elapsed-reading));}
            else RecordTiming("启动失败",(long)Stopwatch.GetElapsedTime(startup).TotalMilliseconds);
            // 进程对象来自本次 Start，Job 只包含此子进程，不按名称选取或影响用户实例。
            if(!process.HasExited){try{process.Kill();}catch(InvalidOperationException)when(process.HasExited){}await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}
            if(drain is not null)try{await drain.ConfigureAwait(false);}catch(OperationCanceledException)when(stopping.IsCancellationRequested){}catch(IOException){}
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public nuint MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcesses;public nuint Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public nuint ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(nint security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimits info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
}
