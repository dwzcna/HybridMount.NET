using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

internal static class UploadCopySnapshot
{
    internal static LocalUpload Create(LocalUpload original,string destination,Action<LocalUpload> validate)
    {
        if(original.IsDirectory)throw new IOException("目录不能另存文件副本。");
        destination=RelativePaths.Normalize(destination);
        if(destination.Length==0||destination.Equals(original.SourcePath,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请填写不同的新文件相对路径。");
        long length=0;
        if(original.SnapshotPath is not null){Validate(original,validate);length=new FileInfo(original.SnapshotPath).Length;}
        return new(Guid.NewGuid().ToString("N"),original.MountId,original.LocalPath,destination,false,original.SnapshotStamp??original.ObservedStamp,1,"Queued",DateTimeOffset.UtcNow,
            SnapshotPath:original.SnapshotPath,SnapshotSha256:original.SnapshotSha256,SnapshotStamp:original.SnapshotStamp,SnapshotIdentity:original.SnapshotIdentity,
            SnapshotGeneration:original.SnapshotPath is null?0:1,Total:length,ExplicitRequest:true,IsCopy:true);
    }
    internal static LocalUpload Prepare(LocalUpload copy,Action<LocalUpload> validate)
    {
        Validate(copy,validate);
        if(copy.Generation!=copy.SnapshotGeneration||new FileInfo(copy.SnapshotPath!).Length!=copy.Total)throw new SourceConflictException("副本快照记录不一致，未改用当前本地内容。");
        return copy with{State="Prepared",SnapshotAttemptId=null,LastCheckedUtc=null};
    }
    private static void Validate(LocalUpload item,Action<LocalUpload> validate)
    {
        if(item.SnapshotPath is null||!File.Exists(item.SnapshotPath)||item.SnapshotSha256 is not {Length:64} hash||!hash.All(Uri.IsHexDigit)||item.SnapshotStamp is null||item.SnapshotIdentity is null||item.SnapshotGeneration<=0)
            throw new SourceConflictException("已保存的上传快照缺失或无效，未改用当前本地内容。");
        validate(item);UploadRules.ValidateTypes(item);
    }
}
