using System.Security.Cryptography;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>仅将已完整的本产品改名文件退出云占位；摘要在根锁外计算，原生转换只占短元数据窗口。</summary>
internal static class CloudExcludedRenameLocalizer
{
    internal sealed record Proof(LocalFileStamp Stamp,string Sha256);
    /// <summary>独占写入／改名的共享范围内读取和刷盘，不写正文；文件必须预先完整，避免在根锁内触发网络。</summary>
    internal static async Task<Proof> VerifyAsync(MountDefinition mount,ExcludedRenameIntent intent,CancellationToken token)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,intent.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(LocalFileState.LinkCount(stream.SafeFileHandle)!=1)throw new SourceConflictException("改名文件有多个硬链接，保留远端供明确处理。");
        var before=LocalFileState.Read(stream.SafeFileHandle);if(before.Identity!=intent.FileId||before.IsDirectory||CloudFilesNative.Inspect(path) is not null&&!CloudFilesNative.HasFullContent(path,before.Length))throw new IOException("改名文件身份变化或内容尚不完整。");
        var digest=Convert.ToHexString(await SHA256.HashDataAsync(stream,token).ConfigureAwait(false));stream.Flush(true);
        var after=LocalFileState.Read(stream.SafeFileHandle);if(UploadRules.ContentChanged(before.Key,after))throw new SourceConflictException("核验期间本地内容变化，本地收尾未完成；云端文件保持。");return new(after,digest);
    }
    /// <summary>调用方持根锁；不计算正文或等待网络，转换前后保全NTFS身份、长度和修改时间。</summary>
    internal static void Detach(MountDefinition mount,ExcludedRenameIntent intent,Proof proof)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,intent.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        using var handle=CloudFilesNative.Open(path,true);if(!CloudFilesNative.CfReferenceProtectedHandle(handle))throw new IOException("无法引用改名文件的保护句柄。");
        try
        {
            using var borrowed=new Microsoft.Win32.SafeHandles.SafeFileHandle(CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle),false);
            var before=LocalFileState.Read(borrowed);var info=CloudFilesNative.Inspect(path);if(before.Identity!=intent.FileId||UploadRules.ContentChanged(proof.Stamp.Key,before)||info is not null&&!CloudFilesNative.HasFullContent(path,before.Length))throw new SourceConflictException("转换前文件变化或内容不完整，未退出占位。");
            var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(info is not null)
            {
                if(marker?.MountId!=mount.Id||marker.EntryId!=intent.EntryId&&marker.EntryId!=intent.UploadId)throw new IOException("改名占位身份不属于已确认条目。");
                CloudFilesNative.Check(CloudFilesNative.CfRevertPlaceholder(handle,0,0));
            }
            var after=LocalFileState.Read(borrowed);if(after.Identity!=before.Identity||after.Length!=before.Length||after.LastWriteTime!=before.LastWriteTime||CloudFilesNative.Inspect(path) is not null)throw new IOException("改名文件退出占位后的身份或内容状态不符。");
        }
        finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
    }
}
