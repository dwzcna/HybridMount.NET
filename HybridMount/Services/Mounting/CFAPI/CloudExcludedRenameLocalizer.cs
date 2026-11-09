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
        var args=NativeRuntimeArguments.Root(mount,new{path,entryId=intent.EntryId,uploadId=intent.UploadId,fileId=intent.FileId,proofStamp=proof.Stamp});
        NativeRuntimeClient.Get().Invoke("detachExcluded",args);
    }

}
