using HybridMount.Models.Mounting;
using System.Text.Json;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    string IMountRepository.ConflictPendingJson(Guid mount,string path) => ConflictPendingJson(mount, path);
    bool IMountRepository.HasConflictHints(Guid mount,string path) => HasConflictHints(mount, path);
    LocalUpload IMountRepository.BeginConflict(LocalUpload? captured,MountEntry? entry,LocalUpload intent,string pending) => BeginConflict(captured, entry, intent, pending);
    LocalUpload IMountRepository.PublishConflictLocal(LocalUpload captured,LocalUpload prepared) => PublishConflictLocal(captured, prepared);
    LocalUpload IMountRepository.CommitConflictRemote(LocalUpload captured,LocalFileStamp finalStamp) => CommitConflictRemote(captured, finalStamp);
}
