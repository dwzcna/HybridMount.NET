namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>不依赖软限额完成旧释放请求；不联网、不上传，根锁内复核上传与远端屏障，锁外通知Shell。</summary>
    private async Task<long> CompletePendingUnpinsAsync(CancellationToken token)
    {
        long freed=0;foreach(var root in _roots.Values)freed+=await CompletePendingUnpinsAsync(root,token).ConfigureAwait(false);return freed;
    }
    private async Task<long> CompletePendingUnpinsAsync(Runtime root,CancellationToken token,IReadOnlyList<MountEntry>? candidates=null)
    {
        long freed=0;
        {
            var changed=new List<string>();await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if(root.Session is null||root.RemovalRequested||root.Definition.RemovalPending)return 0;
                MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
                var entries=_repository.Entries(root.Definition.Id);
                var protectedPaths=_repository.Uploads(root.Definition.Id).Where(x=>x.State!="Completed").Select(x=>x.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in (candidates??entries).Where(e=>e.Materialized&&!e.IsDirectory&&!e.Deleted&&!e.Protected&&e.RemotePresence!=RemotePresence.Deleted))
                {
                    token.ThrowIfCancellationRequested();
                    if(FileExclusionRules.Matches(root.Definition.ExclusionRules,entry.LocalPath)||protectedPaths.Contains(entry.LocalPath)||_repository.HasPendingRemote(root.Definition.Id,entry.SourcePath)||UploadRules.DeletedSourceAncestor(entries,entry.SourcePath) is not null)continue;
                    try
                    {
                        if(root.Session.CompletePendingUnpin(entry) is not {} bytes)continue;
                        freed+=bytes;changed.Add(RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath));
                    }
                    // 占用、身份变化和本地修改留待后续维护，不制造失败上传，也不触发读取。
                    catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException){if(error is not System.ComponentModel.Win32Exception {NativeErrorCode:2 or 3 or 32 or 33})_logger.QueueException("释放空间","Warning","释放请求暂缓，文件继续保护",error,entry.Id);}
                }
            }
            finally
            {
                root.Gate.Release();
                foreach(var path in changed)NotifyFileState(path);
            }
        }
        return freed;
    }
}
