namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>只读核验由应用拥有，配置锁防止同进程的新建或导入跨过核验；取消或退出均保留全部数据。</summary>
    internal Task<SettingsResetPlan> InspectSettingsResetAsync(CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try{return await Task.Run(()=>{EnsureNoSettingsResetMounts();return SettingsResetService.Inspect(cancel);},cancel).ConfigureAwait(false);}
        finally{_configurationGate.Release();}
    },token);
    /// <summary>确认后在相同配置锁内重新核验并发出停止；后续新建、导入及连接无法再被接纳，不在此处删除。</summary>
    internal Task BeginSettingsResetAsync(SettingsResetPlan plan,CancellationToken token)=>OwnAsync(async cancel=>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await Task.Run(()=>{EnsureNoSettingsResetMounts();SettingsResetService.Revalidate(plan,cancel);},cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();RequestStop();
        }
        finally{_configurationGate.Release();}
    },token);
    private void EnsureNoSettingsResetMounts()
    {if(!_roots.IsEmpty||_repository.LoadDefinitions().Count>0)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_0CFCBC69FEFE"));}
}
