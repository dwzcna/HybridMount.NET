namespace HybridMount.Models.Mounting.Windows;

/// <summary>用户明确授权的完整正文替换；所有原生句柄及正文核验留在C++，检查点只持久化阶段。</summary>
internal static class CloudConflictReplacement
{
    internal static async Task<LocalFileStamp> ApplyAsync(MountDefinition mount,LocalUpload intent,Action<LocalFileStamp> commit,Action beginWrite,Action? writing=null,CancellationToken token=default)
    {
        var resolution=intent.Resolution??throw new IOException("冲突取舍记录缺失。");
        var args=NativeRuntimeArguments.Entry(mount,resolution.Entry,new{path=RelativePaths.ToLocal(mount.LocalPath,intent.LocalPath),original=resolution.Original,originalKey=resolution.Original.Key,backupPath=resolution.BackupPath,backupSha256=resolution.BackupSha256,snapshotPath=resolution.RemotePath,sha256=resolution.RemoteSha256,size=resolution.Remote.Size,time=resolution.Remote.ModifiedUtc.UtcDateTime.ToFileTimeUtc()});
        var result=await(await NativeRuntimeClient.GetAsync().ConfigureAwait(false)).InvokeAsync("replaceConflict",args,(stage,data,cancel)=>
        {
            cancel.ThrowIfCancellationRequested();
            if(stage=="beginWrite")beginWrite();else if(stage=="writing")writing?.Invoke();else if(stage=="commit")commit(NativeRuntimeArguments.Stamp(data));else throw new IOException("替换阶段无效。");return Task.FromResult<object?>(null);
        },token).ConfigureAwait(false);return NativeRuntimeArguments.Stamp(result);
    }
}
