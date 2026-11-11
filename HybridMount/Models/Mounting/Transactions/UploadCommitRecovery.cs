using System.Runtime.ExceptionServices;
using System.ComponentModel;
using System.Security.Cryptography;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.Transactions;

/// <summary>根锁内捕获的上传记录及文件戳；只有锁外摘要与捕获内容一致才携带恢复凭据。</summary>
internal sealed record UploadCommitCapture(LocalUpload Row,LocalFileStamp? Stamp,bool NeedsHash)
{
    internal LocalFileStamp? VerifiedStamp {get;init;}
    /// <summary>完整只读摘要且最终戳稳定时才证明存在后续正文；缺失证明不得重新发送。</summary>
    internal bool ContentDifferent {get;init;}
}

/// <summary>原生提交的可选恢复边界；摘要由协调器在锁外完成，适配器在保护句柄内重验状态。</summary>
internal interface IRecoveryUploadCommitter
{
    /// <summary>在根锁内只读判断是否需要恢复核验，不读取完整正文。</summary>
    bool NeedsRecovery(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp stamp);
    /// <summary>在原生保护内复核已验证戳；内容或身份变化时不得授权本地同步完成。</summary>
    LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp? verifiedStamp);
}

/// <summary>协调短锁捕获、锁外异步摘要与短锁发布；过期结果及错误不覆盖新的提交代次。</summary>
internal static class UploadCommitRecovery
{
    /// <summary>只读比对同一路径当前状态；对象已移除返回 false，权限及其他真实错误仍上抛。</summary>
    internal static bool SameStamp(string path,LocalFileStamp stamp)
    {
        try{return LocalFileState.Read(path).Key==stamp.Key;}
        catch(Win32Exception error)when(error.NativeErrorCode is 2 or 3){return false;}
    }
    /// <summary>持锁捕获及重验，释放锁后计算摘要；仅当前尝试继续发布证明或传播失败，取消始终释放根锁。</summary>
    internal static async Task<UploadCommitCapture?> VerifyAsync(SemaphoreSlim gate,Func<UploadCommitCapture?> capture,Func<UploadCommitCapture,bool> current,Func<UploadCommitCapture,Task<string>> hash,CancellationToken token)
    {
        UploadCommitCapture? attempt=null;string? digest=null;ExceptionDispatchInfo? failure=null;
        return await UploadPreparation.RunAsync<UploadCommitCapture>(gate,()=> (attempt=capture()) is not null,async()=>
        {
            if(!attempt!.NeedsHash)return;
            try{digest=await hash(attempt).ConfigureAwait(false);}catch(Exception error){failure=ExceptionDispatchInfo.Capture(error);}
        },()=>
        {
            if(!current(attempt!))return null;
            failure?.Throw();
            return attempt! with{VerifiedStamp=attempt!.NeedsHash&&digest==attempt.Row.SnapshotSha256?attempt.Stamp:null,ContentDifferent=attempt.NeedsHash&&digest is not null&&digest!=attempt.Row.SnapshotSha256};
        },()=>{},token).ConfigureAwait(false);
    }

    /// <summary>使用共享异步句柄核验初始身份并计算 SHA256；调用者发布前及原生提交前必须再验状态戳。</summary>
    /// <param name="checkpoint">实例级测试屏障；每批读取后调用，不持有挂载根锁。</param>
    internal static async Task<string> HashAsync(string path,LocalFileStamp stamp,CancellationToken token,Func<Task>? checkpoint=null)
    {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(LocalFileState.Read(input.SafeFileHandle).Key!=stamp.Key)throw new UploadPreparationChangedException();
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[131072];int count;
        while((count=await input.ReadAsync(buffer,token).ConfigureAwait(false))!=0)
        {
            hash.AppendData(buffer,0,count);
            if(checkpoint is not null)await checkpoint().ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
