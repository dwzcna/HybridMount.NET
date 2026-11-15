using HybridMount.Models.Mounting;
using System.Runtime.ExceptionServices;
using System.ComponentModel;
using System.Security.Cryptography;
namespace HybridMount.Commons.Mounting;

/// <summary>原生提交的可选恢复边界；摘要由协调器在锁外完成，适配器在保护句柄内重验状态。</summary>
internal interface IRecoveryUploadCommitter
{
    /// <summary>在根锁内只读判断是否需要恢复核验，不读取完整正文。</summary>
    bool NeedsRecovery(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp stamp);
    /// <summary>在原生保护内复核已验证戳；内容或身份变化时不得授权本地同步完成。</summary>
    LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp? verifiedStamp);
}
