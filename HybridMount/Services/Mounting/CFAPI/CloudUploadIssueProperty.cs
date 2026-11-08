using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

/// <summary>只写本提供程序的自定义属性。原生共享保护句柄期间核对身份并提交元数据状态戳；不读正文，不改变同步或固定状态。</summary>
internal static class CloudUploadIssueProperty
{
    internal const int PropertyId=1;
    /// <summary>调用方持有根锁及上传路径预留；回调在共享保护仍有效时保存匹配代次，任何失败由独立发布日志记录。</summary>
    internal static async Task SetAsync(MountDefinition mount,LocalUpload upload,Func<LocalFileStamp,bool> begin,Action<LocalFileStamp,LocalFileStamp> commit,CancellationToken token,string message,Func<Task>? written=null)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,upload.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        var native=Inspect(path);
        if(native is {} cloud&&MountPathGuard.ReadEntry(cloud.Identity)?.MountId!=mount.Id)throw new IOException("文件占位身份不属于本挂载，未发布问题。");
        // DELETE仅用于启用Windows共享访问核验，不执行删除。只有属性访问时系统会忽略share约束；加此访问权可阻止外部写入／改名而不请求正文。
        using var borrowed=CloudFilesNative.CreateFile(path,0x10180,1,0,3,0x02200000,0);
        if(borrowed.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var before=LocalFileState.Read(borrowed);var a=upload.Attention!;
        var expected=upload.ObservedStamp.Split(':')[0];
        if(before.Identity!=expected||a.PublishedIdentity is {} previous&&a.ResolvedUtc is not null&&previous!=before.Identity)
            throw new IOException("问题文件身份已变化，未修改替代文件的属性。");
        if(!begin(before))return;
        var text=message;
        text=text.Replace('\r',' ').Replace('\n',' ');if(text.Length>180)text=text[..177]+"…";
        var changed=await CloudItemStateProperty.SetAsync(mount,path,before.IsDirectory,PropertyId,text,
            text.Length==0?"":new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png")).AbsoluteUri,token).ConfigureAwait(false);
        // 普通文件关闭时会再产生USN；在保护内先生成close记录，避免释放后的元数据被误当新编辑。
        if(changed&&!WriteClose(borrowed,0x000900ef,0,0,new byte[8],8,out _,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        var after=LocalFileState.Read(borrowed);var final=Inspect(path);
        if(before.Identity!=after.Identity||before.IsDirectory!=after.IsDirectory||before.Length!=after.Length||before.LastWriteTime!=after.LastWriteTime||
            native?.Info.InSyncState!=final?.Info.InSyncState||native?.Info.PinState!=final?.Info.PinState||native?.Info.OnDiskSize!=final?.Info.OnDiskSize||
            !SameIdentity(native?.Identity,final?.Identity))throw new IOException($"提示写入后的内容或占位状态不一致，未吸收观察状态。before={before.Key}; after={after.Key}; native={native?.Info.InSyncState}/{native?.Info.PinState}/{native?.Info.OnDiskSize}; final={final?.Info.InSyncState}/{final?.Info.PinState}/{final?.Info.OnDiskSize}");
        if(written is not null)await written().ConfigureAwait(false);
        commit(before,after);
    }
    private static bool SameIdentity(byte[]? a,byte[]? b)=>a is null?b is null:b is not null&&a.SequenceEqual(b);
    /// <summary>断开时Windows可能隐藏云重解析标签，直接查询标准占位信息，不能误当普通文件。</summary>
    private static (CloudFilesNative.PlaceholderInfo Info,byte[] Identity)? Inspect(string path)
    {
        using var handle=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer=Marshal.AllocHGlobal(8192);
        try
        {
            var result=CloudFilesNative.CfGetPlaceholderInfo(handle,1,buffer,8192,out _);
            if(result is unchecked((int)0x80070178) or unchecked((int)0x80070182) or unchecked((int)0x80070057))return null;
            // 普通本地文件的常见NOT_A_CLOUD_FILE；其他错误仍保留，避免把权限错误视为普通文件。
            if(result<0){CloudFilesNative.Check(result);return null;}
            var info=Marshal.PtrToStructure<CloudFilesNative.PlaceholderInfo>(buffer);if(info.IdentityLength>4096)throw new IOException("占位身份过长。");
            var identity=new byte[info.IdentityLength];Marshal.Copy(buffer+60,identity,0,identity.Length);return(info,identity);
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [DllImport("kernel32.dll",EntryPoint="DeviceIoControl",SetLastError=true)]
    private static extern bool WriteClose(SafeFileHandle handle,uint control,nint input,uint inputSize,byte[] output,uint outputSize,out uint returned,nint overlap);
}
