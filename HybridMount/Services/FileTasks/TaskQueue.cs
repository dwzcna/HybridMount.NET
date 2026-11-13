using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
namespace HybridMount.Services.FileTasks;

/// <summary>持久步骤就绪与单任务所有权；等待不占云执行位，实际通道／路径租约由执行适配器取得。</summary>
internal sealed class TaskQueue(MountRepository repository)
{
    private sealed class Running(long generation,CancellationToken token)
    {
        internal readonly long Generation=generation;
        internal readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        internal readonly TaskCompletionSource<object?> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _gate=new();
    private readonly Dictionary<string,Running> _running=new(StringComparer.Ordinal);
    private readonly TaskRunner _runner=new();private readonly TaskVerifier _verifier=new();private bool _stopping;
    internal bool IsRunning(string id){lock(_gate)return _running.ContainsKey(id);}
    internal void ReplaceContent(string id,long generation)
    {
        lock(_gate)if(_running.TryGetValue(id,out var current)&&current.Generation<generation)_=current.Stop.CancelAsync();
    }
    /// <summary>相同任务／代次共享在途所有者；新内容等待旧尝试资源归还，再按持久恢复阶段执行。</summary>
    internal async Task<T> RunAsync<T>(FileTaskRecord task,Func<CancellationToken,Task<T>> execute,Func<CancellationToken,Task<TaskVerification>> verify,CancellationToken token)
    {
        while(true)
        {
            Running owner;bool created;
            lock(_gate)
            {
                if(_stopping)throw new OperationCanceledException("文件任务停止接纳。",token);
                created=!_running.TryGetValue(task.Id,out owner!);
                if(created){owner=new(task.Generation,token);_running.Add(task.Id,owner);}
                else if(owner.Generation<task.Generation)_=owner.Stop.CancelAsync();
            }
            if(!created)
            {
                if(owner.Generation==task.Generation)return (T)(await owner.Completion.Task.WaitAsync(token).ConfigureAwait(false))!;
                try{await owner.Completion.Task.WaitAsync(token).ConfigureAwait(false);}catch(Exception)when(!token.IsCancellationRequested){}
                continue;
            }
            _=ExecuteOwnedAsync(owner);return (T)(await owner.Completion.Task.ConfigureAwait(false))!;
        }
        async Task ExecuteOwnedAsync(Running owner)
        {
            string? attempt=null;Exception? failure=null;T? value=default;var ended=false;
            try
            {
                // 业务适配器负责持久恢复阶段；未决写入只能先核对，不由通用队列自动重放。
                if(!repository.PrepareFileTask(task,[task.Operation]))
                {
                    // 候选快照已被新代次取代，无须执行；不以此停止整个挂载的生产者。
                    owner.Completion.TrySetResult(default(T));return;
                }
                attempt=repository.BeginTaskStep(task,0);
                try{value=await _runner.RunAsync(execute,owner.Stop.Token).ConfigureAwait(false);}
                catch(Exception error){failure=error;}
                var result=await _verifier.VerifyAsync(verify,owner.Stop.Token).ConfigureAwait(false);
                if(failure is not null&&result.State is "Queued" or "Running" or "Ready")result=new("Interrupted","execution",failure.HResult);
                repository.EndTaskStep(task,0,attempt,result);ended=true;repository.SetFileTaskState(task,result.State,result.Stage??"verify");
                if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
                owner.Completion.TrySetResult(value);
            }
            catch(Exception error)
            {
                if(attempt is not null&&!ended)
                    try{repository.EndTaskStep(task,0,attempt,new(error is OperationCanceledException?"Interrupted":"OutcomeUnknown","execution",error.HResult));}
                    catch(Exception){/* 先前业务回执保留；下次启动只读核对，不自动重放。 */}
                owner.Completion.TrySetException(error);
            }
            finally
            {
                lock(_gate)_running.Remove(task.Id);owner.Stop.Dispose();
            }
        }
    }
    internal void StopAdmission(){lock(_gate)_stopping=true;}
    internal async Task DrainAsync()
    {
        Running[] work;lock(_gate){_stopping=true;work=_running.Values.ToArray();foreach(var item in work)_=item.Stop.CancelAsync();}
        try{await Task.WhenAll(work.Select(x=>x.Completion.Task)).ConfigureAwait(false);}catch(Exception){/* 逐任务阶段已经保存，退出只等待资源归还。 */}
    }
}
