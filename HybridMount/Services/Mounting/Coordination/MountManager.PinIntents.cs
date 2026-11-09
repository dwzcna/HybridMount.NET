using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly SemaphoreSlim _pinSlots=new(2,2);
    internal Func<CancellationToken,Task>? PinResourcesReleasingForTests {get;set;}
    /// <summary>一个连接拥有一个属性意图消费者；通知只合并唤醒，不进入上传队列，不在监控线程读取文件。</summary>
    private sealed class PinIntentRuntime(CloudMountSession session,CancellationToken token) : IDisposable
    {
        public readonly CloudMountSession Session=session;
        public readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        public readonly SemaphoreSlim Signal=new(0,1);
        public readonly object Sync=new();
        public readonly Dictionary<string,(string Version,DateTimeOffset Next)> Retry=[];
        public FileSystemWatcher? Watcher;
        public Task Work=Task.CompletedTask,Download=Task.CompletedTask,Cancellation=Task.CompletedTask;
        public MountEntry? Active;
        public CancellationTokenSource? DownloadStop;
        private bool _stopping;private int _disposed;
        public void Wake(){lock(Sync){if(!_stopping&&Signal.CurrentCount==0)Signal.Release();}}
        public void RequestStop(){lock(Sync){if(_stopping)return;_stopping=true;Cancellation=Stop.CancelAsync();}}
        public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;Session.PinWorkCompleted-=Wake;Watcher?.Dispose();DownloadStop?.Dispose();Stop.Dispose();Signal.Dispose();}
    }
    private void StartPinIntents(Runtime root)
    {
        lock(root.ConnectionSync)
        {
            if(root.Pins is not null||!root.PinIntentsEnabled||root.Session is null||root.RemovalRequested||root.Definition.RemovalPending||IsStopping)return;
            var runtime=new PinIntentRuntime(root.Session,_stop.Token);root.Pins=runtime;runtime.Session.PinWorkCompleted+=runtime.Wake;
            var watcher=new FileSystemWatcher(root.Definition.LocalPath){IncludeSubdirectories=true,InternalBufferSize=65536,NotifyFilter=NotifyFilters.Attributes|NotifyFilters.FileName|NotifyFilters.DirectoryName};
            // 普通文件沿用原内容监控；只有排除文件的属性变化才额外请求上传扫描以恢复系统标记。
            watcher.Changed+=(_,change)=>
            {
                runtime.Wake();
                if(FileExclusionRules.Matches(root.Definition.ExclusionRules,change.Name??"")&&root.Uploads is {} uploads)
                {uploads.Scans.Record(root.Definition.LocalPath,change,DateTimeOffset.UtcNow);uploads.RequestScan();}
            };
            watcher.Created+=(_,_)=>runtime.Wake();watcher.Deleted+=(_,_)=>runtime.Wake();watcher.Renamed+=(_,_)=>runtime.Wake();watcher.Error+=(_,_)=>runtime.Wake();
            runtime.Watcher=watcher;watcher.EnableRaisingEvents=true;runtime.Wake();runtime.Work=Task.Run(()=>PinIntentLoopAsync(root,runtime));
        }
    }
    private static void RequestPinStop(Runtime root){lock(root.ConnectionSync)root.Pins?.RequestStop();}
    private static async Task StopPinIntentsAsync(Runtime root)
    {
        PinIntentRuntime? runtime;lock(root.ConnectionSync){runtime=root.Pins;if(runtime is null)return;runtime.RequestStop();}
        await Task.WhenAll(runtime.Cancellation,runtime.Work).ConfigureAwait(false);
        lock(root.ConnectionSync)if(ReferenceEquals(root.Pins,runtime))root.Pins=null;runtime.Dispose();
    }
    /// <summary>旧维护单元测试可停止自动消费者，单独验证显式缓存入口；不保存设置或改变原生意图。</summary>
    internal async Task SetPinIntentMonitoringForTestsAsync(Guid id,bool enabled)
    {var root=Root(id);root.PinIntentsEnabled=enabled;if(enabled)StartPinIntents(root);else{RequestPinStop(root);await StopPinIntentsAsync(root).ConfigureAwait(false);}}
    private async Task PinIntentLoopAsync(Runtime root,PinIntentRuntime runtime)
    {
        var token=runtime.Stop.Token;
        try
        {
            while(!token.IsCancellationRequested)
            {
                await runtime.Signal.WaitAsync(TimeSpan.FromSeconds(30),token).ConfigureAwait(false);
                await Task.Delay(100,token).ConfigureAwait(false); // 合并原生递归固定和水合产生的属性事件。
                if(runtime.Download.IsCompleted){await runtime.Download.ConfigureAwait(false);runtime.DownloadStop?.Dispose();runtime.DownloadStop=null;runtime.Active=null;}
                MountEntry? candidate=null;var activeWanted=false;
                await root.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if(!ReferenceEquals(root.Pins,runtime)||!ReferenceEquals(root.Session,runtime.Session)||root.RemovalRequested||root.Definition.RemovalPending)break;
                    var entries=_repository.Entries(root.Definition.Id);
                    foreach(var excluded in entries.Where(x=>x.Materialized&&!x.IsDirectory&&!x.Deleted&&FileExclusionRules.Matches(root.Definition.ExclusionRules,x.LocalPath)))
                        try
                        {
                            if(LocalFileMetadata.TryRead(RelativePaths.ToLocal(root.Definition.LocalPath,excluded.LocalPath)) is not null)
                                ReconcileFileExclusion(root,excluded.LocalPath,LocalFileState.Read(RelativePaths.ToLocal(root.Definition.LocalPath,excluded.LocalPath)),true,token);
                        }
                        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException){root.ExclusionError="系统排除标记未完成："+error.Message;_logger.QueueException("排除标记","Warning","固定维护时排除属性核对失败",error);}
                    var protectedPaths=_repository.Uploads(root.Definition.Id).Where(x=>x.State!="Completed").Select(x=>x.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach(var entry in entries.Where(x=>x.Materialized&&!x.IsDirectory&&!x.Deleted&&!x.Protected&&x.RemotePresence!=RemotePresence.Deleted))
                    {
                        if(FileExclusionRules.Matches(root.Definition.ExclusionRules,entry.LocalPath)||protectedPaths.Contains(entry.LocalPath)||_repository.HasPendingRemote(root.Definition.Id,entry.SourcePath)||UploadRules.DeletedSourceAncestor(entries,entry.SourcePath) is not null)continue;
                        try
                        {
                            var intent=runtime.Session.ReadPinIntent(entry);
                            if(runtime.Active is {} active&&active.Id==entry.Id&&active.Version==entry.Version&&active.LocalPath==entry.LocalPath&&intent.Pinned)activeWanted=true;
                            if(!intent.NeedsDownload||candidate is not null||!runtime.Download.IsCompleted)continue;
                            lock(runtime.Sync)if(runtime.Retry.TryGetValue(entry.Id,out var retry)&&retry.Version==entry.Version&&retry.Next>DateTimeOffset.UtcNow)continue;
                            candidate=entry;
                        }
                        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException){if(error is not System.ComponentModel.Win32Exception {NativeErrorCode:2 or 3 or 32 or 33})_logger.QueueException("固定意图","Warning","固定意图查询暂缓",error,entry.Id);}
                    }
                }
                finally{root.Gate.Release();}
                if(runtime.Active is not null&&!activeWanted&&runtime.DownloadStop is {} cancel)await cancel.CancelAsync().ConfigureAwait(false);
                await CompletePendingUnpinsAsync(root,token).ConfigureAwait(false);
                if(candidate is not null&&runtime.Download.IsCompleted)
                {
                    runtime.Active=candidate;runtime.DownloadStop=CancellationTokenSource.CreateLinkedTokenSource(token);
                    runtime.Download=DownloadPinIntentAsync(root,runtime,candidate,runtime.DownloadStop.Token);
                }
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception error){_logger.QueueException("固定意图","Warning","操作未完成",error);}
        finally{await runtime.Download.ConfigureAwait(false);}
    }
    private async Task DownloadPinIntentAsync(Runtime root,PinIntentRuntime runtime,MountEntry entry,CancellationToken token)
    {
        var entered=false;string? changed=null;
        try
        {
            await _pinSlots.WaitAsync(token).ConfigureAwait(false);entered=true;
            // 会话内单飞拥有保护句柄，网络期间不持根锁；FETCH_DATA继续负责版本和远端屏障检查。
            await runtime.Session.HydratePinnedAsync(entry,token).ConfigureAwait(false);
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var current=_repository.Entries(root.Definition.Id).FirstOrDefault(x=>x.Id==entry.Id);
                if(ReferenceEquals(root.Pins,runtime)&&ReferenceEquals(root.Session,runtime.Session)&&!root.RemovalRequested&&current is not null&&current.Version==entry.Version&&current.LocalPath==entry.LocalPath&&
                    !current.Deleted&&!current.Protected&&!_repository.HasPendingRemote(root.Definition.Id,entry.SourcePath)&&runtime.Session.ReadPinIntent(current) is {Pinned:true,NeedsDownload:false})
                    changed=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);
            }
            finally{root.Gate.Release();}
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception error)
        {lock(runtime.Sync)runtime.Retry[entry.Id]=(entry.Version,DateTimeOffset.UtcNow.AddSeconds(30));_logger.QueueException("固定下载","Warning","后台固定下载未完成",error,entry.Id);}
        finally{if(entered)_pinSlots.Release();if(changed is not null)NotifyFileState(changed);runtime.Wake();}
    }
    /// <summary>原生保护句柄和根锁归还后发布实际状态；失败只影响Shell刷新，不重写同步标记。</summary>
    private void NotifyFileState(string path)
    {try{
        var root=_roots.Values.FirstOrDefault(x=>MountPathGuard.IsUnder(x.Definition.LocalPath,path));
        if(root is not null&&_repository.Entries(root.Definition.Id).FirstOrDefault(x=>!x.Deleted&&RelativePaths.ToLocal(root.Definition.LocalPath,x.LocalPath).Equals(path,StringComparison.OrdinalIgnoreCase)) is {} entry)_previews.RebindMetadata(root.Definition,entry);
        if(ShellNotificationForTests is {} notify)notify(path);else CloudFilesNative.NotifyItemUpdated(path);}catch(Exception error){_logger.Queue("Shell状态","Warning","文件状态已提交，Shell通知失败："+error.GetType().Name);}}
}
