using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HybridMount.Models.Mounting.Content;

/// <summary>借用已引用的 CFAPI 句柄读取本地 PE 资源；不用 FileStream 重新绑定 CFAPI 所属的 I/O 完成端口。</summary>
/// <remarks>单线程同步使用；调用者负责完整性检查、保护引用和句柄生命周期，不能用于远端流式读取。</remarks>
internal sealed class ProtectedResourceStream(nint handle,long length) : Stream
{
    [StructLayout(LayoutKind.Sequential)] private struct Overlap {public nint Internal,InternalHigh;public long Offset;public nint Event;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadFile(nint handle,nint buffer,uint count,out uint read,nint overlap);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetOverlappedResult(nint handle,nint overlap,out uint count,bool wait);
    private long _position;
    public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
    public override long Length=>length;
    public override long Position{get=>_position;set{if(value<0)throw new ArgumentOutOfRangeException(nameof(value));_position=value;}}
    /// <summary>用独立事件等待本次重叠读取；事件低位禁止向原完成端口提交此请求的完成包。</summary>
    public override int Read(byte[] buffer,int offset,int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);ArgumentOutOfRangeException.ThrowIfNegative(count);
        if(offset>buffer.Length-count)throw new ArgumentException("读取缓冲范围无效。");
        count=(int)Math.Min(count,Math.Max(0,length-_position));if(count==0)return 0;
        using var signal=new EventWaitHandle(false,EventResetMode.ManualReset);
        var memory=Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());var pin=GCHandle.Alloc(buffer,GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(new Overlap{Offset=_position,Event=signal.SafeWaitHandle.DangerousGetHandle()|1},memory,false);
            if(!ReadFile(handle,pin.AddrOfPinnedObject()+offset,(uint)count,out var read,memory))
            {
                var code=Marshal.GetLastWin32Error();if(code!=997)throw new Win32Exception(code);
                if(!GetOverlappedResult(handle,memory,out read,true))throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            _position+=read;return checked((int)read);
        }
        finally{pin.Free();Marshal.FreeHGlobal(memory);}
    }
    public override long Seek(long offset,SeekOrigin origin){Position=checked(offset+(origin switch{SeekOrigin.Begin=>0,SeekOrigin.Current=>_position,SeekOrigin.End=>length,_=>throw new ArgumentOutOfRangeException(nameof(origin))}));return Position;}
    public override void Flush(){}
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
