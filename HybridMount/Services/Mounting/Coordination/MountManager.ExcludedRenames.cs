using System.Collections.Concurrent;
using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly ConcurrentDictionary<Guid,ExcludedRenameJournal> _renameJournals=new();
    private readonly ConcurrentDictionary<Guid,(string Error,DateTimeOffset Time)> _renameErrors=new();
#if HYBRIDMOUNT_HEADLESS_TEST
    internal bool ObserveExcludedRenamesForTests {get;set;}=true;
#endif
    internal Func<string,ExcludedRenameIntent,Task>? ExcludedRenameCheckpointForTests {get;set;}
    private ExcludedRenameJournal RenameJournal(Guid id)=>_renameJournals.GetOrAdd(id,key=>new(_repository.DataDirectory,key));
    /// <summary>只读恢复快照；坏日志单独报告，不让普通文件误获改名授权。</summary>
    private IReadOnlyList<ExcludedRenameIntent> RenameIntents(Guid id)
    {
        try{var rows=RenameJournal(id).Read();_renameErrors.TryRemove(id,out _);return rows;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){_renameErrors.AddOrUpdate(id,_=>(error.Message,DateTimeOffset.UtcNow),(_,old)=>old.Error==error.Message?old:(error.Message,DateTimeOffset.UtcNow));return [];}
    }
    private IEnumerable<ExcludedRenameIntent> RenameIntents()=>_roots.Keys.SelectMany(RenameIntents);
    private ExcludedRenameIntent? RenameIntent(string id)=>RenameIntents().FirstOrDefault(x=>x.Id==id);
    private void PublishRename(ExcludedRenameIntent row,Exception? error=null){_repository.SaveOperation(row.Snapshot());RecordStateDiagnostic("排除改名",row.MountId,row.Id,row.Stage,row.Stage is "Failed" or "Conflict" or "OutcomeUnknown" or "Copied"&&row.Error is not null,DiagnosticVersion(row.Error),error);Notify();}
    private ExcludedRenameIntent RenameChange(ExcludedRenameIntent row,Func<ExcludedRenameIntent,ExcludedRenameIntent> change,Exception? error=null)
    {var updated=RenameJournal(row.MountId).Update(row.Id,change)??throw new IOException("改名收尾记录不存在。");PublishRename(updated,error);return updated;}
    private Task RenameCheckpoint(string stage,ExcludedRenameIntent row)=>ExcludedRenameCheckpointForTests?.Invoke(stage,row)??Task.CompletedTask;
    /// <summary>索引保留原云路径，改名日志拥有退出同步意图；新旧路径只阻挡相关代次。</summary>
    private bool RenameBlocksSource(Guid mount,string source){var rows=RenameIntents(mount);return _renameErrors.ContainsKey(mount)||rows.Any(x=>!x.Finished&&(x.SourcePath==source||(x.DestinationPath??x.LocalPath)==source));}
    private IEnumerable<MountOperationSnapshot> RenameJournalIssues()=>_renameErrors.Where(x=>_roots.ContainsKey(x.Key)).Select(x=>new MountOperationSnapshot(x.Key.ToString("N"),"excludedRename","Failed",0,1,[],x.Value.Error,x.Key,ItemPath:"",Attention:new UploadAttention("改名恢复日志无法核对，该挂载暂停新写入；修复日志后可重新核对。"+x.Value.Error,"恢复日志",x.Value.Time,1,"Failed",ShellState:"应用内提示")));
    private bool RenameLegacyChecked(Runtime root){try{return RenameJournal(root.Definition.Id).LegacyChecked;}catch(IOException){RenameIntents(root.Definition.Id);return true;}}
    /// <summary>显式只读核对不重设改名授权；坏日志重读仍不覆盖原文件。</summary>
    internal bool RecheckExcludedRename(string id)
    {
        if(IsStopping)return false;
        if(Guid.TryParseExact(id,"N",out var mount)&&_renameErrors.ContainsKey(mount)){_renameJournals.TryRemove(mount,out _);RenameIntents(mount);Notify();return true;}
        var row=RenameIntent(id);if(row is null||row.Finished)return false;
        if(_roots.TryGetValue(row.MountId,out var root)&&root.Uploads is {} runtime){lock(runtime.Sync)if(runtime.CurrentId==id)return false;}
        RenameChange(row,x=>x with{ReadOnlyRequested=true,CheckedUtc=null});root?.Uploads?.Wake();return true;
    }
    private bool RenameOwnsUpload(LocalUpload upload)=>RenameIntents(upload.MountId).Any(x=>x.UploadId==upload.Id&&x.Stage!="Canceled"&&x.FileId==upload.ObservedStamp.Split(':')[0]);
    private bool RenamedUploadMissing(LocalUpload upload)=>RenameOwnsUpload(upload);
    /// <summary>扫描在任何排除提前返回之前保存关联；只有明确事件或升级前日志能创建远端改名授权。</summary>
    private void ObserveExcludedRename(Runtime root,LocalUploadScanBatch batch,string relative,LocalFileStamp stamp,MountEntry? entry,bool legacy)
    {
        #if HYBRIDMOUNT_HEADLESS_TEST
        if(!ObserveExcludedRenamesForTests)return;
#endif
        if(stamp.IsDirectory)return;
        var journal=RenameJournal(root.Definition.Id);var rows=journal.Read();var active=rows.FirstOrDefault(x=>!x.Finished&&x.FileId==stamp.Identity);
        if(active is not null)
        {
            if(active.LocalPath!=relative)RenameChange(active,x=>x with{LocalPath=relative,CheckedUtc=null});
            return;
        }
        if(!FileExclusionRules.Matches(root.Definition.ExclusionRules,relative))return;
        var notice=batch.Renames.LastOrDefault(x=>x.IntoExcluded!=false&&(x.NewPath==relative||entry is not null&&x.Entry?.Id==entry.Id));
        if(notice is null&&!legacy)return;
        var origin=notice?.Entry??entry;var old=notice?.OldPath??origin?.SourcePath;
        if(string.IsNullOrEmpty(old)||old==relative||FileExclusionRules.Matches(root.Definition.ExclusionRules,old))return;
        var uploads=_repository.Uploads(root.Definition.Id);var upload=uploads.FirstOrDefault(x=>!x.IsCopy&&x.LocalPath==old&&(x.ObservedStamp.Split(':')[0]==stamp.Identity||x.SnapshotIdentity==stamp.Identity));
        var path=RelativePaths.ToLocal(root.Definition.LocalPath,relative);var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        if(origin is not null&&(marker?.MountId!=root.Definition.Id||marker.EntryId!=origin.Id))origin=null;
        // 普通文件经覆盖写入后可能不再带占位标记；精确上传身份与旧版本索引仍可证明原映射。
        if(origin is null&&upload is {EntryId:not null,ExpectedVersion:not null})
            origin=_repository.Entries(root.Definition.Id).FirstOrDefault(x=>x.Id==upload.EntryId&&x.SourcePath==upload.SourcePath&&x.Version==upload.ExpectedVersion&&!x.IsDirectory&&(x.LocalPath==old||x.LocalPath==relative));
        if(origin is null&&upload is null)return;
        if(legacy&&(origin is null||ExclusionMarkers(root).ExistingMark(stamp.Identity,relative) is not {Phase:"Applied"}))return;
        if(origin is not null&&origin.LocalPath!=relative)_repository.SaveEntry(root.Definition.Id,origin with{LocalPath=relative,Protected=true});
        var version=origin?.Version??(upload?.State=="Completed"?upload.ExpectedVersion:null);
        var source=origin?.SourcePath??upload!.SourcePath;
        if(source==relative||FileExclusionRules.Matches(root.Definition.ExclusionRules,source))return;
        // 同一文件与原云版本已经明确取消／完成时，不因重复扫描或规则保存重新创建事务。
        if(rows.Any(x=>x.FileId==stamp.Identity&&x.SourcePath==source&&x.ExpectedVersion==version&&x.LocalPath==relative))return;
        var row=new ExcludedRenameIntent(Guid.NewGuid().ToString("N"),root.Definition.Id,root.Definition.LocalPath,root.Definition.RootIdentity??MountPathGuard.FileIdentity(root.Definition.LocalPath),ExcludedRenameJournal.CurrentOwner,
            stamp.Identity,old,relative,source,origin?.Id,version,upload?.Id,upload?.Generation??0,upload is not null&&UploadRules.InFlight(upload)?"WaitingUpload":"Queued",DateTimeOffset.UtcNow,OriginalVersion:version,WaitForUploadResult:upload is not null&&UploadRules.InFlight(upload),InitialTarget:relative,SourceScope:ExcludedRenameIntent.ScopeFor(root.Definition));
        journal.Add(row);PublishRename(row);
        // Watcher关联晚于一次缺失扫描时，只撤销该扫描生成的取消；用户取消和真实提交失败保持。
        if(upload is {CancelRequested:true,Error:"本地项已删除；未传播远端删除。"}&&UploadRules.InFlight(upload))
            ChangeUpload(upload.Id,x=>x.CancelRequested&&x.Error=="本地项已删除；未传播远端删除。"?x with{CancelRequested=false,Error=null}:x);
        if(upload is not null&&!UploadRules.InFlight(upload)&&upload.State!="Completed")ChangeUpload(upload.Id,x=>x with{State="Canceled",CancelRequested=true,Error="已改名为排除文件，原上传停止，云端旧文件由独立任务核对。"});
    }
    private bool? RetryExcludedRename(string id)
    {
        var row=RenameIntent(id);if(row is null)return null;if(IsStopping||row.Stage is not("Failed" or "Conflict" or "OutcomeUnknown" or "Copied")||row.Stage is "OutcomeUnknown" or "Copied"&&row.CheckedUtc is null)return false;
        // 核对期间不能覆盖其请求；终态已持久发布后可接纳下一次显式重试，无需等待消费者归还槽位。
        if(row.ReadOnlyRequested)return false;
        RenameChange(row,x=>x with{RetryRequested=true,CancelRequested=false,ReadOnlyRequested=false,Stage=x.RemoteMoved?"RemoteMoved":x.MoveStarted?"OutcomeUnknown":"Queued",CheckedUtc=null});
        if(_roots.TryGetValue(row.MountId,out var root))root.Uploads?.Wake();return true;
    }
    private bool? CancelExcludedRename(string id)
    {
        var row=RenameIntent(id);if(row is null)return null;if(IsStopping||row.Finished)return false;
        RenameChange(row,x=>x with{CancelRequested=true,CheckedUtc=null});if(_roots.TryGetValue(row.MountId,out var root)){root.Uploads?.CancelRequest(id);root.Uploads?.Wake();}return true;
    }
    /// <summary>新同名对象建立独立上传代次；旧改名事务不能让它继承已清理的远端基线。</summary>
    private bool IsNewObjectAtRenamedSource(Guid mount,string relative,LocalFileStamp stamp)=>RenameIntents(mount).Any(x=>x.Stage!="Canceled"&&(x.OldLocalPath==relative||x.SourcePath==relative)&&x.FileId!=stamp.Identity);
    private bool HasOwnRenameEntry(Guid mount,string entry)=>RenameIntents(mount).Any(x=>!x.Finished&&(x.EntryId==entry||x.UploadId==entry));
}
