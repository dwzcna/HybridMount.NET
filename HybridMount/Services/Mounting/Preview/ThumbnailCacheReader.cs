using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>供独立缩略图宿主读取的本机缓存适配器；只查身份和元数据，不创建应用服务、不水合或联网。</summary>
internal sealed class ThumbnailCacheReader(string database,bool perMount=false)
{
    private readonly ThumbnailCacheStore _store=new(database);
    internal static string CurrentOwner=>WindowsIdentity.GetCurrent().User?.Value??throw new IOException("无法取得当前账户身份。");
    /// <summary>只把连接配置摘要存入库；两种运行模式仍各自持有配置，不互相接管根。</summary>
    internal static ThumbnailCacheRoot RootFor(MountDefinition mount)
    {
        var scope=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{mount.Source,mount.CredentialRevision})));
        return new(mount.Id,Path.GetFullPath(mount.LocalPath),mount.RootIdentity??MountPathGuard.FileIdentity(mount.LocalPath),CurrentOwner,scope,mount.ThumbnailsEnabled&&!mount.RemovalPending);
    }
    /// <summary>捕获本产品当前账户的单个占位文件；这里只读元数据，是否完整由生成路径另行核对。</summary>
    internal static ThumbnailCacheBinding? Capture(ThumbnailCacheRoot root,MountEntry entry)
    {
        try
        {
            if(!root.Enabled||root.Owner!=CurrentOwner||entry.Deleted||entry.IsDirectory||!entry.Materialized||entry.RemotePresence==RemotePresence.Deleted||!PreviewWorker.Supports(entry.LocalPath))return null;
            var path=RelativePaths.ToLocal(root.Path,entry.LocalPath);MountPathGuard.ValidateOperation(new(root.MountId,"",root.Path,RootIdentity:root.FileId),path);
            var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(marker?.MountId!=root.MountId||marker.EntryId!=entry.Id)return null;
            var stamp=LocalFileState.Read(path);if(stamp.IsDirectory)return null;
            var clean=!entry.Protected&&info!.Value.Info.InSyncState==1&&info.Value.Info.ModifiedSize==0;
            var revision=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{entry.Version,Local=clean?null:stamp.Key})));
            return new(root,entry.Id,path,stamp.Identity,stamp.Length,stamp.LastWriteTime,stamp.Key,revision,clean,stamp.Usn);
        }
        catch(Exception error)when(MetadataFailure(error)){return null;}
    }
    /// <summary>精确戳防止同名替换或保留时间戳的编辑复用旧图；固定／脱水后由已确认的应用操作重绑。</summary>
    internal static bool Valid(ThumbnailCacheBinding binding)
    {
        try
        {
            var root=binding.Root;if(!root.Enabled||root.Owner!=CurrentOwner||!Path.IsPathFullyQualified(root.Path)||root.Path.StartsWith("\\",StringComparison.Ordinal)||!Path.IsPathFullyQualified(binding.Path)||!MountPathGuard.IsUnder(root.Path,binding.Path))return false;
            MountPathGuard.ValidateOperation(new(root.MountId,"",root.Path,RootIdentity:root.FileId),binding.Path);
            var info=CloudFilesNative.InspectPersisted(binding.Path);var marker=MountPathGuard.ReadEntry(info.Identity);
            if(marker?.MountId!=root.MountId||marker.EntryId!=binding.EntryId)return false;
            var stamp=LocalFileState.Read(binding.Path);if(stamp.Identity!=binding.FileId||stamp.IsDirectory||stamp.Length!=binding.Length||stamp.LastWriteTime!=binding.LastWriteTime)return false;
            // Shell只读访问可能延后刷新ChangeTime而USN不变；仅干净内容且可靠USN相同时接受，修改正文仍要求精确戳。
            if(stamp.Key!=binding.Stamp&&(!binding.Clean||binding.Usn<=0||stamp.Usn!=binding.Usn))return false;
            return binding.Clean?info.Info.InSyncState==1&&info.Info.ModifiedSize==0:CloudFilesNative.HasFullContent(binding.Path,stamp.Length);
        }
        catch(Exception error)when(MetadataFailure(error)){return false;}
    }
    internal byte[]? TryGet(string path,int size)
    {
        if(size is <1 or >256||!Path.IsPathFullyQualified(path)||path.StartsWith("\\",StringComparison.Ordinal))return null;
        try
        {
            if(!perMount)return _store.Read(Path.GetFullPath(path),size,Valid);
            var root=MountPathGuard.ReadRoot(path);if(root is null||root.Owner!=CurrentOwner)return null;
            return new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(database,root.MountId)).Read(Path.GetFullPath(path),size,Valid);
        }
        catch(Exception error)when(MetadataFailure(error)){return null;}
    }
    private static bool MetadataFailure(Exception error)=>error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or ArgumentException;
}
