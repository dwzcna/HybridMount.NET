using System.Text.Json;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>每次连接建立独立配额所有者；不持根锁执行网络，旧代次的迟到结果不更新新连接。</summary>
    private void StartCapacity(Runtime root)
    {
        RemoteCapacitySession capacity;
        lock(root.ConnectionSync)
        {
            if(IsStopping||root.Source is null||root.Session is null||root.Capacity is not null||root.Definition.RemovalPending)return;
            var scope=JsonSerializer.Serialize(new{root.Definition.Source,root.Definition.CredentialRevision});
            if(root.CapacityScope!=scope){root.CapacityStatus=null;root.CapacityScope=scope;}
            var source=root.Source;var session=root.Session;
            capacity=new(source,value=>
            {
                lock(root.ConnectionSync)
                {
                    if(IsStopping||root.Capacity is not {IsStopping:false}||!ReferenceEquals(root.Source,source)||!ReferenceEquals(root.Session,session)||root.CapacityScope!=scope)return;
                    root.CapacityStatus=value;
                }
                Notify();
            },_stop.Token,root.CapacityStatus);
            root.Capacity=capacity;
        }
        _=capacity.RefreshAsync(force:true);
    }
    /// <summary>页面按需读取五分钟缓存；调用方取消仅退出等待，断开或退出才取消共享查询。</summary>
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
}
