using HybridMount.Models.Mounting;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal string ConflictPendingJson(Guid mount,string path);
    internal bool HasConflictHints(Guid mount,string path);
    internal LocalUpload BeginConflict(LocalUpload? captured,MountEntry? entry,LocalUpload intent,string pending);
    internal LocalUpload PublishConflictLocal(LocalUpload captured,LocalUpload prepared);
    internal LocalUpload CommitConflictRemote(LocalUpload captured,LocalFileStamp finalStamp);
}
