using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private const string RemoteDeletedMessage="远端已删除此路径或父目录；保留本地内容，禁止自动重新创建。请刷新核对或另存到其他路径。";
    private static bool SourceContains(string parent,string path)=>parent==path||path.StartsWith(parent+"/",StringComparison.Ordinal);
    private static bool InFlight(LocalUpload item)=>item.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown";
    private static bool Unfinished(LocalUpload item)=>item.State is not("Completed" or "Canceled" or "Ignored");
    private static MountEntry? DeletedSourceAncestor(IEnumerable<MountEntry> entries,string path)
        =>entries.FirstOrDefault(entry=>entry.RemotePresence==RemotePresence.Deleted&&SourceContains(entry.SourcePath,path));
    /// <summary>完整枚举之后逐项确认缺失；确认阶段不修改索引或本地文件，任一请求失败不会应用半份删除结果。</summary>
    private async Task<IReadOnlyList<MountEntry>> ConfirmRemoteDeletionsAsync(Runtime root,IReadOnlyDictionary<string,SourceEntry> snapshot,CancellationToken token)
    {
        var missing=new List<MountEntry>();
        foreach(var entry in _repository.Entries(root.Definition.Id).Where(entry=>entry.SourcePath.Length!=0&&!snapshot.ContainsKey(entry.SourcePath)).OrderBy(entry=>entry.SourcePath.Count(c=>c=='/')))
        {
            token.ThrowIfCancellationRequested();
            if(missing.Any(parent=>parent.IsDirectory&&SourceContains(parent.SourcePath,entry.SourcePath))) {missing.Add(entry);continue;}
            if(await root.Source!.TryGetMetadataAsync(entry.SourcePath,token).ConfigureAwait(false) is null)missing.Add(entry);
        }
        return missing;
    }
    /// <summary>调用方持有根锁；先记录删除屏障并停止未发送任务，再从子项到父目录按身份清理。</summary>
    private IReadOnlyList<MountItemResult> ApplyRemoteDeletions(Runtime root,IReadOnlyList<MountEntry> missing,CancellationToken token)
    {
        var results=new List<MountItemResult>();var mount=root.Definition;
        foreach(var entry in missing)
        {
            token.ThrowIfCancellationRequested();
            if(!_repository.SetRemotePresence(mount.Id,entry,RemotePresence.Deleted))throw new IOException("确认删除期间映射发生变化，请重试刷新。");
        }
        foreach(var upload in _repository.Uploads(mount.Id).Where(item=>missing.Any(entry=>SourceContains(entry.SourcePath,item.SourcePath))&&Unfinished(item)))
        {
            if(InFlight(upload)){root.Uploads?.CancelRequest(upload.Id);continue;}
            if(upload.State!="Conflict"||upload.Error!=RemoteDeletedMessage)ChangeUpload(upload.Id,item=>item with{State="Conflict",Error=RemoteDeletedMessage,LastCheckedUtc=null});
        }
        foreach(var old in missing.OrderByDescending(entry=>entry.LocalPath.Count(c=>c=='/')).ThenBy(entry=>entry.IsDirectory?1:0))
        {
            token.ThrowIfCancellationRequested();var entry=_repository.Entries(mount.Id).First(item=>item.Id==old.Id);
            var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);
            try
            {
                MountPathGuard.ValidateOperation(mount,mount.LocalPath);
                if(LocalMissing(path))
                {
                    if(entry.Materialized){_repository.CompleteRemoteCleanup(mount.Id,entry);results.Add(new(entry.LocalPath,"Completed","远端已删除，本地项已不存在。"));}
                    continue;
                }
                MountPathGuard.ValidateOperation(mount,path);
                var active=_repository.Uploads(mount.Id).Any(item=>Unfinished(item)&&
                    (SourceContains(entry.SourcePath,item.SourcePath)||SourceContains(entry.LocalPath,item.LocalPath)));
                if(active)throw new IOException("条目存在待上传、冲突或结果不明的内容，已保留本地文件。");
                if(!entry.Materialized||entry.Protected||entry.Deleted)throw new IOException("条目含本地新增、修改或删除意图，已保留。");
                if(root.Session!.IsActive(entry.Id))throw new IOException("条目正在使用，暂缓清理；关闭后再次刷新。");
                CloudRemoteCleanup.Delete(mount,entry);
                if(!LocalMissing(path))throw new IOException("删除仍待系统完成，请稍后刷新。");
                _repository.CompleteRemoteCleanup(mount.Id,entry);results.Add(new(entry.LocalPath,"Completed","远端已删除，本地已同步占位或缓存已清理（含已固定内容）。"));
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {results.Add(new(entry.LocalPath,"Conflict",error.Message));}
        }
        return results;
    }
    private static string RefreshSummary(IReadOnlyList<MountItemResult> results)
    {
        if(results.Count==0)return "远端刷新完成。";
        var retained=results.Count(item=>item.Status!="Completed");return $"远端删除处理：{results.Count-retained} 项已清理，{retained} 项保留；详情见刷新任务。";
    }
    /// <summary>扫描不会把远端删除的旧内容当作新文件；新修改仍受保护并显示可处理的冲突。</summary>
    private void ObserveBlockedUpload(Runtime root,MountEntry? entry,LocalUpload? prior,string relative,LocalFileStamp stamp,bool clean)
    {
        if(InFlightOrCompleted(prior)&&clean)return;
        if(prior is null)
        {
            if(!clean)SaveUpload(new(Guid.NewGuid().ToString("N"),root.Definition.Id,relative,relative,stamp.IsDirectory,stamp.Key,1,"Conflict",DateTimeOffset.UtcNow,entry?.Id,entry?.Version,Error:RemoteDeletedMessage));
        }
        else if(!InFlight(prior)&&(!clean||Unfinished(prior)))
        {
            if(prior.State!="Conflict"||prior.Error!=RemoteDeletedMessage||prior.ObservedStamp!=stamp.Key)
                ChangeUpload(prior.Id,item=>item with{State="Conflict",ObservedStamp=stamp.Key,Generation=item.Generation+(item.ObservedStamp==stamp.Key?0:1),Error=RemoteDeletedMessage,LastCheckedUtc=null});
        }
        if(entry is not null&&!clean&&!entry.Protected)_repository.SaveEntry(root.Definition.Id,entry with{Protected=true});
    }
    private static bool InFlightOrCompleted(LocalUpload? item)=>item is not null&&(InFlight(item)||item.State is "Completed" or "Canceled" or "Ignored");
    /// <summary>在根锁内校验目标及已知父目录；不会为更新任务或远端删除记录自动 MKCOL。</summary>
    private async Task PrepareUploadDestinationAsync(Runtime root,LocalUpload item,CancellationToken token)
    {
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(root.RemovalRequested||root.Session is null||root.Source is null)throw new OperationCanceledException(token);
            var entries=_repository.Entries(root.Definition.Id);
            if(DeletedSourceAncestor(entries,item.SourcePath) is not null)throw new SourceConflictException(RemoteDeletedMessage);
            var known=entries.FirstOrDefault(entry=>entry.SourcePath==item.SourcePath);
            if(item.ExpectedVersion is not null)
            {
                if(await root.Source.TryGetMetadataAsync(item.SourcePath,token).ConfigureAwait(false) is null)throw new SourceConflictException("远端更新目标已删除，不能改为新增；请刷新或另存副本。");
            }
            else if(known is not null&&!item.IsDirectory)throw new SourceConflictException("目标已有历史远端基线，本任务不能按新增重新创建；请刷新核对。");
            var parent="";
            foreach(var part in RelativePaths.Parent(item.SourcePath).Split('/',StringSplitOptions.RemoveEmptyEntries))
            {
                parent=parent.Length==0?part:parent+"/"+part;
                var metadata=await root.Source.TryGetMetadataAsync(parent,token).ConfigureAwait(false);
                if(metadata is null)
                {
                    if(item.ExpectedVersion is not null||entries.Any(entry=>entry.SourcePath==parent))throw new SourceConflictException("已知远端父目录已删除，未自动恢复目录。请刷新核对。");
                    metadata=await root.Source.CreateDirectoryAsync(parent,token).ConfigureAwait(false);
                }
                if(!metadata.IsDirectory)throw new SourceConflictException("远端父路径已是文件，不能创建子项。");
            }
        }
        finally{root.Gate.Release();}
    }
    /// <summary>快照校验完成后，在根锁内重新核对屏障和任务状态再进入发送阶段；刷新先完成时绝不覆盖其冲突状态。</summary>
    internal async Task<LocalUpload?> BeginUploadSendAsync(Guid mountId,LocalUpload prepared,CancellationToken token=default)
    {
        var root=Root(mountId);await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(root.RemovalRequested||root.Session is null||root.Source is null)throw new OperationCanceledException(token);
            var current=_repository.Upload(prepared.Id);if(current is null)return null;
            if(DeletedSourceAncestor(_repository.Entries(mountId),current.SourcePath) is not null)throw new SourceConflictException(RemoteDeletedMessage);
            if(current.CancelRequested||(!root.SchedulingEnabled&&!current.ExplicitRequest)||current.State!="Prepared"||current.SnapshotStamp!=prepared.SnapshotStamp||current.SnapshotGeneration!=prepared.SnapshotGeneration)return null;
            if(current.Generation!=current.SnapshotGeneration)
            {ChangeUpload(current.Id,item=>item with{State="Queued",Error=null,LastCheckedUtc=null});return null;}
            return ChangeUpload(current.Id,item=>item with{State="Sending",Error=null,Bytes=0,LastCheckedUtc=null});
        }
        finally{root.Gate.Release();}
    }

}
