using System.Security.Cryptography;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>准备期间会话或根定义已替换；丢弃本次结果，保留新会话状态。</summary>
internal sealed class UploadPreparationChangedException:Exception;

/// <summary>在根锁内捕获和发布准备意图，正文复制与远端父目录请求在锁外执行；取消和失败总会清理本次暂存。</summary>
internal static class UploadPreparation
{
    /// <summary>短锁捕获、锁外工作、短锁条件发布；退出及捕获失败也执行清理，不持锁等待网络。</summary>
    internal static async Task<T?> RunAsync<T>(SemaphoreSlim gate,Func<bool> capture,Func<Task> work,Func<T?> publish,Action cleanup,CancellationToken token) where T:class
    {
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try{token.ThrowIfCancellationRequested();if(!capture())return null;}finally{gate.Release();}
            await work().ConfigureAwait(false);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try{token.ThrowIfCancellationRequested();return publish();}finally{gate.Release();}
        }
        finally{cleanup();}
    }
    /// <summary>仅当前代次、取消状态及实例尝试均一致时允许发布。</summary>
    internal static bool SameAttempt(LocalUpload current,LocalUpload captured)
        =>captured.SnapshotAttemptId is not null&&!current.CancelRequested&&current==captured;

    /// <summary>以仓库原子更新再次核对捕获记录；不覆盖较新编辑或取消。</summary>
    internal static LocalUpload? PublishAttempt(LocalUpload captured,Func<Func<LocalUpload,LocalUpload>,LocalUpload?> update,Func<LocalUpload,LocalUpload> publish)
    {
        var accepted=false;
        var result=update(current=>
        {
            if(!SameAttempt(current,captured))return current;
            accepted=true;return publish(current);
        });
        return accepted?result:null;
    }

    /// <summary>只读共享句柄复制并核对同一对象的状态戳，返回不可变快照摘要；调用者负责失败清理。</summary>
    internal static async Task<string> CopyAsync(string path,string snapshot,LocalFileStamp stamp,CancellationToken token,Func<Task>? checkpoint=null)
    {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(LocalFileState.Read(input.SafeFileHandle).Key!=stamp.Key)throw new IOException("文件在准备快照时变化，等待稳定后重试。");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using(var output=new FileStream(snapshot,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.Asynchronous|FileOptions.SequentialScan))
        {
            var buffer=new byte[131072];int count;
            while((count=await input.ReadAsync(buffer,token).ConfigureAwait(false))!=0)
            {
                hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false);
                if(checkpoint is not null)await checkpoint().ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false);output.Flush(true);
        }
        if(LocalFileState.Read(input.SafeFileHandle).Key!=stamp.Key||LocalFileState.Read(path).Key!=stamp.Key)throw new IOException("文件在复制时变化，等待稳定后重试。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    /// <summary>每次远端父目录操作前后重新核对挂载、上传意图及删除屏障，已知更新目标不自动恢复。</summary>
    internal static async Task PrepareParentsAsync(IFileSource source,LocalUpload item,Func<string?,Task> validate,CancellationToken token)
    {
        await validate(null).ConfigureAwait(false);
        if(item.ExpectedVersion is not null&&await source.TryGetMetadataAsync(item.SourcePath,token).ConfigureAwait(false) is null)
            throw new SourceConflictException("远端更新目标已删除，不能改为新增；请刷新或另存副本。");
        var parent="";
        foreach(var part in RelativePaths.Parent(item.SourcePath).Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            parent=parent.Length==0?part:parent+"/"+part;
            await validate(null).ConfigureAwait(false);
            var metadata=await source.TryGetMetadataAsync(parent,token).ConfigureAwait(false);
            if(metadata is null)
            {
                if(item.ExpectedVersion is not null)throw new SourceConflictException("已知远端父目录已删除，未自动恢复目录。请刷新核对。");
                await validate(parent).ConfigureAwait(false);
                metadata=await source.CreateDirectoryAsync(parent,token).ConfigureAwait(false);
            }
            if(!metadata.IsDirectory)throw new SourceConflictException("远端父路径已是文件，不能创建子项。");
        }
        await validate(null).ConfigureAwait(false);
    }
}
