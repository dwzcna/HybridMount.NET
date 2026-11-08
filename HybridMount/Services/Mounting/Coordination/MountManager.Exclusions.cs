using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private sealed class UploadExcludedException():IOException("文件匹配排除规则，未开始上传。");
    private FileExclusionMarkerStore ExclusionMarkers(Runtime root){lock(root.ConnectionSync)return root.ExclusionMarkers??=new(_repository.DataDirectory,root.Definition.Id);}
    /// <summary>保存规则与发送阶段共用根锁；在途事务继续收尾，未发送准备取消，失败保存保持原定义。</summary>
    public Task SetFileExclusionRulesAsync(Guid id,IReadOnlyList<FileExclusionRule> rules,CancellationToken token=default)
    {
        var validated=FileExclusionRules.Validate(rules);
        return OwnAsync(async cancel=>
        {
            var root=Root(id);await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                await root.Gate.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    cancel.ThrowIfCancellationRequested();if(root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("正在移除的挂载不能修改排除规则。");
                    var definition=root.Definition with{ExclusionRules=Array.AsReadOnly(validated)};_repository.SaveDefinition(definition);root.Definition=definition;
                    foreach(var item in _repository.Uploads(id))
                    {
                        var matched=FileExclusionRules.Matches(definition,item);
                        var next=matched?FileExclusionRules.Pause(item):FileExclusionRules.Resume(item);
                        if(next!=item){ChangeUpload(item.Id,x=>x==item?next:x);if(next.State=="Excluded")root.Uploads?.CancelRequest(item.Id);}
                    }
                }
                finally{root.Gate.Release();}
            }
            finally{_configurationGate.Release();}
            root.Uploads?.RequestFullScan();root.Pins?.Wake();Notify();
            if(root.Session is not null)await Task.Run(()=>ScanLocalUploadsAsync(root,cancel),cancel).ConfigureAwait(false);
        },token);
    }
    /// <summary>规则保存结果中的系统标记摘要；不读取文件或联网。</summary>
    internal string FileExclusionStatus(Guid id)
    {
        var root=Root(id);try{return (root.Session is null?"未连接；系统标记将在连接后更新。":"")+(root.ExclusionError??ExclusionMarkers(root).Describe());}
        catch(Exception error){return "系统排除标记日志读取失败："+error.Message;}
    }
    /// <summary>调用方持根锁；吸收已证明没有正文变化的自有属性戳，不推进上传代次或改变内容基线。</summary>
    private LocalFileStamp ReconcileFileExclusion(Runtime root,string relative,LocalFileStamp stamp,bool excluded,CancellationToken token)
    {
        if(stamp.IsDirectory)return stamp;
        // 已发送事务先收尾；不在快照提交窗口修改属性，规则仍立即阻止后续发送。
        try
        {
            var markers=ExclusionMarkers(root);if(!excluded&&!markers.HasMarks)return stamp;
            if(_repository.Uploads(root.Definition.Id).Any(x=>!x.IsCopy&&x.LocalPath==relative&&UploadRules.InFlight(x)))return stamp;
            var result=markers.Reconcile(root.Definition,relative,excluded,token);root.ExclusionError=null;
            if(result.Before is null||result.After is null)return stamp;
            // 属性句柄关闭也可能推进USN；归还后再读取，连同已记录的纯元数据变化一起核实。
            var after=LocalFileState.Read(RelativePaths.ToLocal(root.Definition.LocalPath,relative));
            if(!UploadRules.ContentChanged(result.Before.Key,after))
                foreach(var item in _repository.Uploads(root.Definition.Id).Where(x=>x.LocalPath==relative&&x.ObservedStamp!=after.Key&&!UploadRules.ContentChanged(x.ObservedStamp,result.Before)))
                    _repository.UpdateUpload(item.Id,x=>x!=item?x:x with{ObservedStamp=after.Key,
                        SnapshotStamp=x.SnapshotStamp is not null&&x.SnapshotIdentity==after.Identity&&x.SnapshotGeneration==x.Generation&&!UploadRules.ContentChanged(x.SnapshotStamp,result.Before)?after.Key:x.SnapshotStamp});
            if(result.Changed){root.Pins?.Wake();NotifyFileState(RelativePaths.ToLocal(root.Definition.LocalPath,relative));}
            return after;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {root.ExclusionError="系统排除标记未完成（上传排除仍生效）："+relative+"；"+error.Message;return stamp;}
    }
    /// <summary>每次辅助写入前重新检查当前规则，防止保存规则后旧准备继续发起探测或目标写入。</summary>
    private async Task EnsureUploadIncludedAsync(Runtime root,LocalUpload item,CancellationToken token)
    {
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try{if(RenameOwnsUpload(item))throw new UploadExcludedException();if(FileExclusionRules.Matches(root.Definition,item)){ChangeUpload(item.Id,FileExclusionRules.Pause);throw new UploadExcludedException();}}
        finally{root.Gate.Release();}
    }
}
