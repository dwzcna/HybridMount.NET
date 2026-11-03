using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>无凭据的媒体解码工作端；只通过继承的标准管道向父进程请求有界字节。</summary>
public static class PreviewWorker
{
    public static readonly int[] Sizes=[32,64,128,256];
    public static bool Supports(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".mp4" or ".exe";
    /// <summary>单次解码，所有尺寸共用同一父进程预算；失败只回传类型状态，不回传堆栈或原消息。</summary>
    public static int Run()
    {
        using var input=Console.OpenStandardInput();using var output=Console.OpenStandardOutput();
        try
        {
            var request=IpcProtocol.ReadAsync<PreviewWorkerPacket>(input).GetAwaiter().GetResult();
            if(request.Kind!="decode"||request.Length<0||request.Extension is null||!Supports("file"+request.Extension))return 2;
            using var stream=new BrokerStream(input,output,request.Length);var frames=new Dictionary<int,byte[]>();
            foreach(var size in Sizes)
            {
                stream.Position=0;byte[]? png;
                if(request.Extension==".exe")png=ExeIconExtractor.Extract(stream,size);
                else
                {
                    var status=Decode(new ComReadStream(stream),request.Extension,(uint)size,out var memory,out var count);
                    try{if(status<0||memory==0||count is <8 or >ExePreviewCache.MaximumPngBytes)throw new IOException("系统解码不可用。");png=new byte[count];Marshal.Copy(memory,png,0,(int)count);}finally{if(memory!=0)Marshal.FreeCoTaskMem(memory);}
                }
                if(png is null)throw new IOException("未取得媒体帧。");frames[size]=png;
            }
            IpcProtocol.WriteAsync(output,new PreviewWorkerPacket("result",Frames:frames)).GetAwaiter().GetResult();return 0;
        }
        catch{try{IpcProtocol.WriteAsync(output,new PreviewWorkerPacket("unsupported")).GetAwaiter().GetResult();}catch{}return 1;}
    }
    [DllImport("HybridMount.Ext.dll",EntryPoint="HybridMountDecodePreview",CharSet=CharSet.Unicode)]
    private static extern int Decode([MarshalAs(UnmanagedType.Interface)]IStream stream,string extension,uint size,out nint png,out uint length);
    private sealed class BrokerStream(Stream input,Stream output,long length,object? shared=null) : Stream
    {
        private long _position;
        private readonly object _sync=shared??new();
        public Stream Clone()=>new BrokerStream(input,output,length,_sync){Position=_position};
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>length;
        public override long Position {get=>_position;set{if(value<0||value>length)throw new IOException("预览寻址越界。");_position=value;}}
        public override int Read(byte[] buffer,int offset,int count)
        {
            count=(int)Math.Min(Math.Min(count,PreviewRangeBudget.WindowBytes),length-_position);if(count==0)return 0;
            lock(_sync)
            {
            IpcProtocol.WriteAsync(output,new PreviewWorkerPacket("read",_position,count)).GetAwaiter().GetResult();
            var response=IpcProtocol.ReadAsync<PreviewWorkerPacket>(input).GetAwaiter().GetResult();
            if(response.Kind!="data"||response.Data is null||response.Data.Length!=count)throw new IOException("父进程拒绝预览读取。");
            response.Data.CopyTo(buffer,offset);_position+=count;return count;
            }
        }
        public override long Seek(long offset,SeekOrigin origin){Position=checked((origin switch{SeekOrigin.Begin=>0,SeekOrigin.Current=>_position,SeekOrigin.End=>length,_=>throw new ArgumentOutOfRangeException(nameof(origin))})+offset);return _position;}
        public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    /// <summary>经典 COM IStream 只读适配器，供 WIC／MF 的同步随机读取回调；所有读取仍受父进程预算约束。</summary>
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class ComReadStream(Stream stream) : IStream
    {
        public void Read(byte[] buffer,int count,nint read){var actual=stream.Read(buffer,0,count);if(read!=0)Marshal.WriteInt32(read,actual);}
        public void Seek(long move,int origin,nint position){var value=stream.Seek(move,(SeekOrigin)origin);if(position!=0)Marshal.WriteInt64(position,value);}
        public void Stat(out STATSTG stat,int flags){stat=new(){type=2,cbSize=stream.Length,grfMode=0};}
        public void Clone(out IStream copy)=>copy=new ComReadStream(stream is BrokerStream broker?broker.Clone():stream);
        public void CopyTo(IStream target,long count,nint read,nint written){long total=0;var buffer=new byte[65536];while(total<count){var size=stream.Read(buffer,0,(int)Math.Min(buffer.Length,count-total));if(size==0)break;target.Write(buffer,size,0);total+=size;}if(read!=0)Marshal.WriteInt64(read,total);if(written!=0)Marshal.WriteInt64(written,total);}
        public void Commit(int flags){}public void Revert()=>throw new NotSupportedException();public void LockRegion(long offset,long count,int kind)=>throw new NotSupportedException();public void UnlockRegion(long offset,long count,int kind)=>throw new NotSupportedException();public void SetSize(long value)=>throw new NotSupportedException();public void Write(byte[] buffer,int count,nint written)=>throw new NotSupportedException();
    }
}
