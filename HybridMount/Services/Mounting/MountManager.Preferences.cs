using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;
public sealed partial class MountManager
{
    /// <summary>后台保存启动连接开关；失败保留原定义，不断开当前连接，不改变其他连接参数。</summary>
    public Task SetAutoConnectAsync(Guid id,bool enabled,CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);var root=Root(id);await root.Gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if(root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("移除过程中不能更改启动连接。");
            var definition=root.Definition with{AutoConnect=enabled};_repository.SaveDefinition(definition);root.Definition=definition;
        }
        finally{root.Gate.Release();}
        Notify();
    },token);
}
