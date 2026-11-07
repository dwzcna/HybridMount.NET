using System.ComponentModel;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

/// <summary>会话内有界的一次性自写入戳；两秒内完全匹配才过滤文件事件，过期或读取失败继续正常扫描。</summary>
internal sealed class LocalWriteStampCache(int capacity=4096)
{
    private readonly object _sync=new();
    private readonly Dictionary<string,(LocalFileStamp Stamp,DateTimeOffset Expires)> _expected=new(StringComparer.Ordinal);
    /// <summary>记录已确认的文件写入；无USN及目录不参与，容量耗尽时清空缓存并回归正常扫描。</summary>
    internal void Record(string path,LocalFileStamp stamp,DateTimeOffset now)
    {
        if(capacity<=0||stamp.IsDirectory||stamp.Usn==0)return;
        lock(_sync)
        {
            if(_expected.Count>=capacity&&!_expected.ContainsKey(path))_expected.Clear();
            _expected[path]=(stamp,now.AddSeconds(2));
        }
    }
    /// <summary>删除或重命名事件使该路径的预期写入失效。</summary>
    internal void Forget(string path){lock(_sync)_expected.Remove(path);}
    /// <summary>全量扫描、失败或重置时清除短期证明，不影响任何上传记录。</summary>
    internal void Clear(){lock(_sync)_expected.Clear();}
    /// <summary>先原子移除再锁外读取属性；完整戳匹配才消费事件，比较期间新记录的戳保留给下一批。</summary>
    internal bool Consume(string path,DateTimeOffset now,Func<LocalFileStamp> read)
    {
        (LocalFileStamp Stamp,DateTimeOffset Expires) expected;
        lock(_sync)if(!_expected.Remove(path,out expected))return false;
        if(now>=expected.Expires)return false;
        try{return read() is {IsDirectory:false} stamp&&stamp==expected.Stamp;}
        catch(Exception error)when(error is IOException or Win32Exception or UnauthorizedAccessException){return false;}
    }
    /// <summary>仅申请READ_ATTRIBUTES，以OPEN_REPARSE_POINT和OPEN_NO_RECALL打开；不读取正文或触发水合，调用方释放句柄。</summary>
    internal static SafeFileHandle OpenRead(string path)
    {
        var handle=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);
        if(!handle.IsInvalid)return handle;
        var error=System.Runtime.InteropServices.Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);
    }
    /// <summary>读取同一属性句柄的文件戳并释放；路径缺失、占用及权限错误由消费者回退正常扫描。</summary>
    internal static LocalFileStamp Read(string path){using var handle=OpenRead(path);return LocalFileState.Read(handle);}
}
