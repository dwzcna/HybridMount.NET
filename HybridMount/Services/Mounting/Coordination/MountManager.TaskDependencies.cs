using System.Collections.Concurrent;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly ConcurrentDictionary<string,string> _operationChecks=new();
    private readonly ConcurrentDictionary<string,byte> _operationChecking=new();
    /// <summary>只读合并实际持久依赖与当前连接状态；不清除原错误，不因界面查询改变任务或连接。</summary>
    private MountOperationSnapshot DescribeOperationWait(MountOperationSnapshot snapshot,string path)
    {
        string? reason=null,blockerId=null;
        if(snapshot.MountId is {} mount&&_roots.TryGetValue(mount,out var root)&&snapshot.State is not("Completed" or "Superseded"))
        {
            var blocker=RemotePendingFor(root).Where(x=>x.Id!=snapshot.Id&&x.State is not("Completed" or "Superseded")&&!(x.State=="Canceled"&&x.MetadataOnly)&&UploadRules.SourceContains(x.Path,path)).OrderByDescending(x=>x.Path.Length).FirstOrDefault();
            if(blocker is not null){blockerId=blocker.Id;reason=$"等待远端任务 {blocker.Id}（{blocker.Path}，{RemoteTask(blocker).DisplayState}）：{blocker.Error??"远端变化尚待核对"}";}
            else if(snapshot.State is "Queued" or "Prepared" or "Waiting" or "WaitingCapacity")
            {
                if(root.Session is null)reason="等待挂载连接。";
                else if(_repository.Upload(snapshot.Id) is {} upload)
                {
                    if(_repository.HasPendingRemote(mount,path))reason="等待协议变更提示完成核对。";
                    else if(RenameBlocksSource(mount,path))reason="等待同路径改名收尾。";
                    else if(!root.SchedulingEnabled&&!upload.ExplicitRequest)reason="自动上传已暂停，等待恢复调度。";
                    else if(upload.NextAttemptUtc is {} next&&next>DateTimeOffset.UtcNow)reason="等待重试时间："+next.ToLocalTime().ToString("G");
                    else if(UploadRules.DeletedSourceAncestor(_repository.Entries(mount),path) is {} parent)reason="等待已删除父路径核对："+parent.SourcePath;
                    else if(root.Uploads is {} runtime){lock(runtime.Sync)if(runtime.CurrentId is {} current&&current!=snapshot.Id){blockerId=current;reason="等待当前上传结束："+current;}}
                }
            }
        }
        return snapshot with{WaitingReason=reason,WaitingTaskId=blockerId,InspectionMessage=_operationChecks.GetValueOrDefault(snapshot.Id)};
    }
    /// <summary>登记只读核对并由应用拥有；不得改写上传条件或重启已取消文件操作。目录证明只解除误依赖，真实冲突继续保留。</summary>
    public bool RecheckOperation(string id)
    {
        if(IsStopping)return false;if(RenameIntent(id) is not null)return RecheckExcludedRename(id);
        var upload=_repository.Upload(id);var pending=_roots.Values.SelectMany(RemotePendingFor).FirstOrDefault(x=>x.Id==id);
        var mount=upload?.MountId??pending?.MountId;
        if(mount is null||!_roots.TryGetValue(mount.Value,out var root)||root.RemovalRequested||root.Definition.RemovalPending||root.RemoteSync is not {} runtime||!_operationChecking.TryAdd(id,0))return false;
        _operationChecks[id]="正在只读核对；未授权覆盖或重试。";Notify();
        _=OwnAsync(async token=>
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,runtime.Stop.Token);
            using var cancelOwner=TasksQueue.RegisterCancellation(id,()=>{if(!linked.IsCancellationRequested)_=linked.CancelAsync();return true;});
            try
            {
                await runtime.Serial.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    var path=upload?.SourcePath??pending!.Path;
                    foreach(var directory in RemotePendingFor(root).Where(x=>x.State=="Canceled"&&!x.MetadataOnly&&x.Entry is {IsDirectory:true}&&UploadRules.SourceContains(x.Path,path)))
                        await RecheckCanceledDirectoryAsync(root,runtime,directory,linked.Token).ConfigureAwait(false);
                    var current=await runtime.Source.TryGetMetadataAsync(path,linked.Token).ConfigureAwait(false);
                    _operationChecks[id]=current is null?"只读核对：远端当前不存在，原任务与写入条件保留。":"只读核对完成；原任务、版本条件和冲突选择保留。";
                }
                finally{runtime.Serial.Release();}
            }
            catch(OperationCanceledException)when(linked.IsCancellationRequested){_operationChecks[id]="只读核对已停止。";}
            catch(Exception error){_operationChecks[id]="只读核对失败："+error.GetType().Name;ReportDiagnosticException("任务核对","只读核对未完成",error);}
            finally{_operationChecking.TryRemove(id,out _);Notify();}
        },_stop.Token);
        return true;
    }
}
