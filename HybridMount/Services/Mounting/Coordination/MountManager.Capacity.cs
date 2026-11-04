using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly CapacityCoordinator _capacities=new();
    private static string CapacityKey(MountDefinition definition)=>JsonSerializer.Serialize(new{definition.Id,definition.Source,definition.CredentialRevision,definition.CapacityAutoDetectEnabled,definition.MaximumCapacityBytes});
    private static RemoteCapacity CapacityInitial(MountDefinition definition)=>new(!CapacityRules.Enabled(definition)?"未设置容量限制":definition.CapacityAutoDetectEnabled?"自动识别服务器配额":"手动配置上限（尚未核实用量）",ConfiguredTotalBytes:definition.CapacityAutoDetectEnabled?null:definition.MaximumCapacityBytes);
    /// <summary>关闭且无手动值时不查询网络；每次连接和设置变更建立独立所有者，迟到结果不得跨代发布。</summary>
    private void StartCapacity(Runtime root)
    {
        RemoteCapacitySession capacity;
        lock(root.ConnectionSync)
        {
            if(IsStopping||root.Source is null||root.Session is null||root.Capacity is not null||root.Definition.RemovalPending)return;
            var definition=root.Definition;var scope=CapacityKey(definition);
            if(root.CapacityScope!=scope){root.CapacityStatus=null;root.CapacityScope=scope;}
            if(!CapacityRules.Enabled(definition)){root.CapacityStatus=new(CapacityInitial(definition));return;}
            var source=root.Source;var session=root.Session;
            capacity=new(source,value=>
            {
                lock(root.ConnectionSync)
                {
                    if(IsStopping||root.Capacity is not {IsStopping:false}||!ReferenceEquals(root.Source,source)||!ReferenceEquals(root.Session,session)||root.CapacityScope!=scope)return;
                    root.CapacityStatus=value;
                }
                if(!value.IsRefreshing&&value.Error is null)WakeCapacityWaiters(FileSourceFactory.CapacityWriteScope(definition));
                Notify();
            },_stop.Token,root.CapacityStatus,query:async(force,cancel)=>
            {
                using var lease=await _capacities.EnterAsync(FileSourceFactory.CapacityWriteScope(definition),cancel).ConfigureAwait(false);
                return await ReadCapacityAsync(lease,definition,source,force,cancel).ConfigureAwait(false);
            });
            root.Capacity=capacity;
        }
        _=capacity.RefreshAsync(force:true);
    }
    /// <summary>保存设置不探测连接；退出纳入应用所有者，失败保持旧定义，停止旧查询时不持根锁。</summary>
    public Task SetCapacityAsync(Guid id,bool automatic,long? maximumBytes,CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        var root=Root(id);CapacityRules.Validate(root.Definition.Source?.Kind,automatic,maximumBytes);
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await root.Gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if(root.RemovalRequested||root.Definition.RemovalPending||root.Definition.Source?.Kind is not(FileSourceKind.WebDav or FileSourceKind.S3))throw new IOException("当前挂载不能更改容量设置。");
                var definition=root.Definition with{CapacityAutoDetectEnabled=automatic,MaximumCapacityBytes=maximumBytes};
                _repository.SaveDefinition(definition);root.Definition=definition;
                lock(root.ConnectionSync){root.CapacityScope=null;root.CapacityStatus=new(CapacityInitial(definition));}
                Interlocked.Exchange(ref root.CapacityForce,1);
            }
            finally{root.Gate.Release();}
            RequestCapacityStop(root);await StopCapacityAsync(root).ConfigureAwait(false);StartCapacity(root);
            WakeCapacityWaiters(FileSourceFactory.CapacityWriteScope(root.Definition));Notify();
        }
        finally{_configurationGate.Release();}
    },token);
    /// <summary>页面按需读取五分钟缓存；单个等待者取消不取消共享查询，强制刷新也核实上传等待。</summary>
    public Task RefreshCapacityAsync(Guid id,bool force=false,CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        var root=Root(id);RemoteCapacitySession? capacity;lock(root.ConnectionSync)capacity=root.Capacity;
        if(capacity is not null)await capacity.RefreshAsync(force,cancel).ConfigureAwait(false);
    },token);
    private static void RefreshCapacity(Runtime root){RemoteCapacitySession? capacity;lock(root.ConnectionSync)capacity=root.Capacity;_=capacity?.RefreshAsync(force:true);}
    private static void RequestCapacityStop(Runtime root){lock(root.ConnectionSync)root.Capacity?.RequestStop();}
    private static async Task StopCapacityAsync(Runtime root)
    {
        RemoteCapacitySession? capacity;lock(root.ConnectionSync){capacity=root.Capacity;capacity?.RequestStop();}
        if(capacity is null)return;await capacity.DisposeAsync().ConfigureAwait(false);
        lock(root.ConnectionSync)if(ReferenceEquals(root.Capacity,capacity))root.Capacity=null;
    }
    private async Task<RemoteCapacity> ReadCapacityAsync(CapacityCoordinator.Lease lease,MountDefinition definition,IFileSource source,bool force,CancellationToken token)
        =>await lease.ReadAsync(CapacityKey(definition),async cancel=>
        {
            if(definition.CapacityAutoDetectEnabled)return await source.GetRemoteCapacityAsync(cancel).ConfigureAwait(false);
            if(source is not IRemoteUsageSource usage)throw new NotSupportedException("此文件源尚不能完整统计用量。");
            var value=await usage.GetRemoteUsageAsync(cancel).ConfigureAwait(false);
            if(!value.IsComplete||value.UsedBytes<0)throw new IOException("容量用量尚未完整核实。");
            var maximum=definition.MaximumCapacityBytes??throw new IOException("尚未配置容量上限。");
            return new("手动配置上限 · "+value.Scope,Math.Max(0,maximum-value.UsedBytes),value.UsedBytes,maximum);
        },force,token).ConfigureAwait(false);
    /// <summary>不改变取消或冲突；只清除容量等待的重试时间，文件仍由扫描核对当前代次。</summary>
    private void WakeCapacityWaiters(string scope)
    {
        foreach(var root in _roots.Values.Where(x=>FileSourceFactory.CapacityWriteScope(x.Definition)==scope))
        {
            foreach(var item in _repository.Uploads(root.Definition.Id).Where(x=>x.State=="WaitingCapacity"&&!x.CancelRequested))ChangeUpload(item.Id,x=>x.State=="WaitingCapacity"?x with{LastCheckedUtc=null}:x);
            root.Uploads?.Wake();
        }
    }
    private void PublishCapacity(Runtime root,MountDefinition definition,RemoteCapacitySnapshot? snapshot)
    {
        if(snapshot is null)return;
        lock(root.ConnectionSync){if(IsStopping||root.Session is null||CapacityKey(root.Definition)!=CapacityKey(definition))return;root.CapacityStatus=snapshot;}
        Notify();
    }
    private sealed class CapacityWaitingException(string message):IOException(message);
    private sealed class CapacitySettingsChangedException():IOException("容量设置已变化，重新准备检查。");
    /// <summary>租约作为本进程容量预留，持有至请求阶段落库；同桶其他挂载须等候或重新统计。</summary>
    private sealed class UploadCapacityPermit(CapacityCoordinator.Lease lease,MountDefinition definition,string? key,long oldLength):IDisposable
    {
        public MountDefinition Definition {get;}=definition;
        public void Committed(SourceEntry receipt)=>lease.Committed(key,receipt.IsDirectory?0:receipt.Size-oldLength);
        public RemoteCapacitySnapshot? Snapshot=>lease.Snapshot(key);
        public void Invalidate()=>lease.Invalidate();
        public void Dispose()=>lease.Dispose();
    }
    /// <summary>所有容量网络读取均在根锁外，先核对原基线及正增长，再允许辅助写入或正文发送。</summary>
    private async Task<UploadCapacityPermit> EnterUploadCapacityAsync(Runtime root,LocalUpload item,CancellationToken token)
    {
        var definition=root.Definition;var scope=FileSourceFactory.CapacityWriteScope(definition);var source=root.Source??throw new OperationCanceledException(token);
        var lease=await _capacities.EnterAsync(scope,token).ConfigureAwait(false);long oldLength=0;var enabled=CapacityRules.Enabled(definition);string? key=null;
        try
        {
            if(enabled&&!item.IsDirectory)
            {
                SourceEntry? metadata;
                try{metadata=await source.TryGetMetadataAsync(item.SourcePath,token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                catch(Exception error){throw new CapacityWaitingException("等待核实容量：目标长度查询未完成（"+error.GetType().Name+"）。");}
                if(item.ExpectedVersion is null&&metadata is not null)throw new SourceConflictException("远端目标已存在，不能按新增覆盖。");
                if(item.ExpectedVersion is not null)
                {
                    if(metadata is null||metadata.IsDirectory||metadata.Size<0||!source.IsSameVersion(item.ExpectedVersion,metadata.Version))throw new SourceConflictException("远端基线或类型已变化，容量检查未放行上传。");
                    oldLength=metadata.Size;
                }
                var growth=CapacityRules.Growth(oldLength,item.Total);key=CapacityKey(definition);
                if(growth>0)
                {
                    var uncertain=_repository.Uploads().Any(x=>x.Id!=item.Id&&x.State is "Sending" or "OutcomeUnknown"&&_roots.TryGetValue(x.MountId,out var other)&&FileSourceFactory.CapacityWriteScope(other.Definition)==scope);
                    if(uncertain)throw new CapacityWaitingException("等待核实容量：同一远端范围仍有写入结果待核对。");
                    RemoteCapacity value;
                    try{value=await ReadCapacityAsync(lease,definition,source,Interlocked.Exchange(ref root.CapacityForce,0)!=0,token).ConfigureAwait(false);}
                    catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                    catch(Exception error){throw new CapacityWaitingException("等待核实容量：完整用量查询未完成（"+error.GetType().Name+"）。");}
                    PublishCapacity(root,definition,lease.Snapshot(key));
                    if(value.AvailableBytes is not >=0)throw new CapacityWaitingException("等待核实容量：服务器未提供可靠可用量。");
                    if(growth>value.AvailableBytes.Value)throw new CapacityWaitingException($"容量不足：本次净增长 {TaskByteFormat.Format(growth)}，可用 {TaskByteFormat.Format(value.AvailableBytes.Value)}。");
                }
            }
            return new(lease,definition,key,oldLength);
        }
        catch{lease.Dispose();throw;}
    }
}
