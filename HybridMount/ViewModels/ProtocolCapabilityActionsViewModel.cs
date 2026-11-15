using CommunityToolkit.Mvvm.Input;

namespace HybridMount.ViewModels;

/// <summary>能力检测及清理按钮的可用状态与工作所有者；运行中的检测始终保留取消命令。</summary>
public sealed class ProtocolCapabilityActionsViewModel : IDisposable
{
    private Func<Task>? _start;
    private Action? _cancel;
    private Func<Guid,Task>? _cleanup;
    private Action<Exception>? _error;
    private bool _disposed;
    private readonly Dictionary<Guid,Task> _work = [];
    public Task WhenIdle => Task.WhenAll(_work.Values);
    public bool Available {get;private set;}
    public bool Running {get;private set;}
    public IRelayCommand ProbeCommand {get;}
    public IRelayCommand<Guid> CleanupCommand {get;}
    public Task ExecutionTask {get;private set;}=Task.CompletedTask;
    public ProtocolCapabilityActionsViewModel()
    {
        ProbeCommand=new RelayCommand(()=>{if(_disposed||!(Available||Running))return;if(Running)_cancel?.Invoke();else if(_start is {} start)StartOwned(start);},()=>!_disposed&&(Available||Running));
        CleanupCommand=new RelayCommand<Guid>(id=>{if(_disposed||!Available||Running)return;if(_cleanup is {} cleanup)StartOwned(()=>cleanup(id));},_=>!_disposed&&Available&&!Running);
    }
    public void SetHandlers(Func<Task> start,Action cancel,Func<Guid,Task> cleanup,Action<Exception> error){_start=start;_cancel=cancel;_cleanup=cleanup;_error=error;}
    public void Publish(bool available,bool running)
    {
        var probe=ProbeCommand.CanExecute(null);var cleanup=CleanupCommand.CanExecute(Guid.Empty);
        Available=available;Running=running;
        if(probe!=ProbeCommand.CanExecute(null))ProbeCommand.NotifyCanExecuteChanged();
        if(cleanup!=CleanupCommand.CanExecute(Guid.Empty))CleanupCommand.NotifyCanExecuteChanged();
    }
    private void StartOwned(Func<Task> work)
    {
        var owner=Guid.NewGuid();_work[owner]=Task.CompletedTask;
        var task=RunAsync(owner,work);ExecutionTask=task;if(_work.ContainsKey(owner))_work[owner]=task;
    }
    private async Task RunAsync(Guid owner,Func<Task> work){try{await work();}catch(OperationCanceledException){}catch(Exception error){if(!_disposed)_error?.Invoke(error);}finally{_work.Remove(owner);}}
    public void Dispose(){if(_disposed)return;_disposed=true;ProbeCommand.NotifyCanExecuteChanged();CleanupCommand.NotifyCanExecuteChanged();_start=null;_cancel=null;_cleanup=null;_error=null;}
}
