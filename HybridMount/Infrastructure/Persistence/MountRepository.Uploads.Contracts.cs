using HybridMount.Models.Mounting;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    IReadOnlyList<LocalUpload> IMountRepository.UnfinishedUploads(Guid? mount) => UnfinishedUploads(mount);
    IReadOnlyList<LocalUpload> IMountRepository.UploadsNeedingAttention(Guid? mount) => UploadsNeedingAttention(mount);
    IReadOnlyList<LocalUpload> IMountRepository.UploadsAtPath(Guid mount, string path) => UploadsAtPath(mount, path);
    int IMountRepository.UploadAttentionCount(Guid mount) => UploadAttentionCount(mount);
    Dictionary<Guid, int> IMountRepository.UploadAttentionCounts() => UploadAttentionCounts();
    void IMountRepository.RecoverUploadAttention() => RecoverUploadAttention();
    LocalUpload? IMountRepository.TryCommitUploadedEntry(LocalUpload captured,LocalUpload prepared,MountEntry entry,SourceEntry receipt,LocalFileStamp? committedStamp,bool missing,bool contentChanged) => TryCommitUploadedEntry(captured, prepared, entry, receipt, committedStamp, missing, contentChanged);
}
