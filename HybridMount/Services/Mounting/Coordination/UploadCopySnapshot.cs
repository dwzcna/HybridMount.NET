using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>副本冻结规则；复用已保存的不可变快照，不把之后编辑吸收进副本，缺失证据时拒绝回退。</summary>
internal static class UploadCopySnapshot
{
    /// <summary>从原记录构造新ID、新目标的独立意图，保留快照证据；无快照沿用对话框已明确说明的准备时内容。</summary>
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
    /// <summary>验证已保存快照的归属、类型、长度及代次，返回Prepared；实际摘要由发送工作器在只读句柄中核对。</summary>
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
