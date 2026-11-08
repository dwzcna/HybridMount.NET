using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>用户明确授权的正文替换；独立于普通远端刷新，不放宽普通自动对账的本地修改保护。</summary>
internal static class CloudConflictReplacement
{
    /// <summary>完整备份和远端摘要已确认后，在同一保护引用内重验正文、替换、核验并持久提交；不改变文件ID或Pin意图。</summary>
    internal static LocalFileStamp Apply(MountDefinition mount,LocalUpload intent,Action<LocalFileStamp> commit,Action beginWrite,Action? writing=null)
    {
        var resolution=intent.Resolution??throw new IOException("冲突取舍记录缺失。");var path=RelativePaths.ToLocal(mount.LocalPath,intent.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        using var backup=new FileStream(resolution.BackupPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(backup.Length!=resolution.Original.Length||Convert.ToHexString(SHA256.HashData(backup))!=resolution.BackupSha256)throw new IOException("本地恢复备份摘要变化，拒绝替换原文件。");
        using var snapshot=new FileStream(resolution.RemotePath!,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(snapshot.Length!=resolution.Remote.Size||Convert.ToHexString(SHA256.HashData(snapshot))!=resolution.RemoteSha256)throw new IOException("远端暂存摘要变化，拒绝替换。");snapshot.Position=0;
        using var handle=CloudFilesNative.Open(path,true);if(!CloudFilesNative.CfReferenceProtectedHandle(handle))throw new IOException("无法取得文件保护引用，请关闭占用后重试。");
        try
        {
            var raw=CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle);using var borrowed=new SafeFileHandle(raw,false);var before=LocalFileState.Read(borrowed);
            if(LocalFileState.LinkCount(borrowed)!=1)throw new SourceConflictException("文件具有多个硬链接，未覆盖；请先保存为独立文件。 ");
            if(before.Key!=resolution.Original.Key||before.IsDirectory)throw new SourceConflictException("本地文件在确认后变化，未覆盖。");
            var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(info is not null&&(marker?.MountId!=mount.Id||marker.EntryId!=resolution.Entry.Id))throw new SourceConflictException("占位身份变化，未替换。");
            if(info is not null&&!CloudFilesNative.HasFullContent(path,before.Length))throw new SourceConflictException("本地正文不完整，不能建立可恢复备份。");
            using(var original=new ProtectedResourceStream(raw,before.Length))if(Convert.ToHexString(SHA256.HashData(original))!=resolution.BackupSha256)throw new SourceConflictException("本地备份与当前正文不一致，未替换。");
            beginWrite(); // 全部正文及备份核验完成后才记录不可逆阶段，失败准备不冒充已开始写入。
            ProtectedContentWriter.Replace(raw,snapshot,resolution.Remote.Size,resolution.Remote.ModifiedUtc,writing);
            using(var actual=new ProtectedResourceStream(raw,resolution.Remote.Size))if(Convert.ToHexString(SHA256.HashData(actual))!=resolution.RemoteSha256)throw new IOException("替换后摘要未通过，保留恢复备份和保护屏障。");
            if(info is null)
            {
                var identity=JsonSerializer.SerializeToUtf8Bytes(new MountPathGuard.EntryMarker(mount.Id,resolution.Entry.Id));
                CloudFilesNative.Check(ConvertToPlaceholder(handle,identity,(uint)identity.Length,0,out _,0));
            }
            long usn=0;CloudFilesNative.Check(SetInSync(handle,1,0,ref usn));
            var final=LocalFileState.Read(borrowed);if(final.Identity!=before.Identity)throw new IOException("替换后文件身份变化，保留恢复屏障。");
            if(info is not null&&CloudFilesNative.Inspect(path)?.Info.PinState!=info.Value.Info.PinState)throw new IOException("替换期间固定意图变化，请重新核对。");
            commit(final);return final;
        }
        finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
    }
    [DllImport("CldApi.dll",EntryPoint="CfConvertToPlaceholder")]private static extern int ConvertToPlaceholder(CloudFilesNative.ProtectedHandle handle,byte[] identity,uint size,uint flags,out long usn,nint overlap);
    [DllImport("CldApi.dll",EntryPoint="CfSetInSyncState")]private static extern int SetInSync(CloudFilesNative.ProtectedHandle handle,uint state,uint flags,ref long usn);
}
