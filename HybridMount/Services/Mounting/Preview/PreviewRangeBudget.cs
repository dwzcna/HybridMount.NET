using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>单文件跨解码尺寸共享 8MiB／32 次网络读取预算；256KiB 窗口缓存合并解码器的细碎随机读取。</summary>
public sealed class PreviewRangeBudget
{
    public const int MaximumBytes=8*1024*1024,MaximumRequests=32,WindowBytes=256*1024;
    private readonly Dictionary<long,byte[]> _windows=[];
    private readonly long _length;
    private int _maximumBytes=MaximumBytes,_maximumRequests=MaximumRequests;
    /// <summary>完整本地内容使用独立128MiB／512次预算，远端默认预算保持不变。</summary>
    public static PreviewRangeBudget ForLocal(long length,Func<long,Memory<byte>,CancellationToken,ValueTask<PreviewRangeResult>> read)=>new(length,read){_maximumBytes=128*1024*1024,_maximumRequests=512};
    private readonly Func<long,Memory<byte>,CancellationToken,ValueTask<PreviewRangeResult>> _read;
    public PreviewRangeBudget(IFileSource source,SourceEntry entry):this(entry.Size,(offset,buffer,token)=>source.TryReadPreviewRangeAsync(entry,offset,buffer,token)){}
    public PreviewRangeBudget(long length,Func<long,Memory<byte>,CancellationToken,ValueTask<PreviewRangeResult>> read){_length=length;_read=read;}
    public int Bytes {get;private set;}
    public int Requests {get;private set;}
    /// <summary>由一个工作进程串行调用；范围不合法、能力不足或预算用尽时失败，不读取原占位内容。</summary>
    public async Task<byte[]> ReadAsync(long offset,int count,CancellationToken token)
    {
        if(offset<0||offset>_length||count<0||count>WindowBytes)throw new IOException("预览工作进程的范围无效。");
        count=(int)Math.Min(count,_length-offset);var output=new byte[count];var copied=0;
        while(copied<count)
        {
            token.ThrowIfCancellationRequested();var position=offset+copied;var start=position/WindowBytes*WindowBytes;
            if(!_windows.TryGetValue(start,out var window))
            {
                var length=(int)Math.Min(WindowBytes,_length-start);if(Requests>=_maximumRequests||Bytes+length>_maximumBytes)throw new IOException("预览读取预算已用尽。",unchecked((int)0x80070718));
                Requests++;Bytes+=length;window=new byte[length];var result=await _read(start,window,token).ConfigureAwait(false);
                if(!result.Supported||result.Bytes!=length)throw new IOException("服务未提供严格范围预览，使用普通图标。",unchecked((int)0x80004001));_windows.Add(start,window);
            }
            var begin=(int)(position-start);var take=Math.Min(count-copied,window.Length-begin);window.AsSpan(begin,take).CopyTo(output.AsSpan(copied));copied+=take;
        }
        return output;
    }
}
