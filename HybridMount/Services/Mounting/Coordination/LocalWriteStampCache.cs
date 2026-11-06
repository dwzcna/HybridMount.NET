using System.ComponentModel;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

internal sealed class LocalWriteStampCache(int capacity=4096)
{
    private readonly object _sync=new();
    private readonly Dictionary<string,(LocalFileStamp Stamp,DateTimeOffset Expires)> _expected=new(StringComparer.Ordinal);
    internal void Record(string path,LocalFileStamp stamp,DateTimeOffset now)
    {
        if(capacity<=0||stamp.IsDirectory||stamp.Usn==0)return;
        lock(_sync)
        {
            if(_expected.Count>=capacity&&!_expected.ContainsKey(path))_expected.Clear();
            _expected[path]=(stamp,now.AddSeconds(2));
        }
    }
    internal void Forget(string path){lock(_sync)_expected.Remove(path);}
    internal void Clear(){lock(_sync)_expected.Clear();}
    internal bool Consume(string path,DateTimeOffset now,Func<LocalFileStamp> read)
    {
        (LocalFileStamp Stamp,DateTimeOffset Expires) expected;
        lock(_sync)if(!_expected.Remove(path,out expected))return false;
        if(now>=expected.Expires)return false;
        try{return read() is {IsDirectory:false} stamp&&stamp==expected.Stamp;}
        catch(Exception error)when(error is IOException or Win32Exception or UnauthorizedAccessException){return false;}
    }
    internal static SafeFileHandle OpenRead(string path)
    {
        var handle=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);
        if(!handle.IsInvalid)return handle;
        var error=System.Runtime.InteropServices.Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);
    }
    internal static LocalFileStamp Read(string path){using var handle=OpenRead(path);return LocalFileState.Read(handle);}
}
