using HybridMount.Models.Mounting;
namespace HybridMount.Commons;

/// <summary>在调用方根锁内核验并提交本地上传状态；成功返回保护范围内的稳定戳，变化时返回空值，失败保留恢复证据。</summary>
public interface ILocalUploadCommitter
{
    /// <summary>调用方已持根锁；返回仍对应快照的稳定戳，类型冲突或原生失败继续抛出。</summary>
    /// <param name="mount">已核验本产品身份的挂载。</param>
    /// <param name="entry">本次远端回执对应的条目。</param>
    /// <param name="prepared">保存原始基线及不可变快照的上传意图。</param>
    /// <returns>保护句柄内取得的状态；内容或身份变化则为空。</returns>
    LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared);
}
