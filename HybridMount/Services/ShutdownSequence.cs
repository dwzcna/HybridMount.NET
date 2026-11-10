namespace HybridMount.Services;

/// <summary>宿主按阶段执行清理；保留调用方上下文，单阶段失败不会跨越托盘 async void 边界或跳过后续资源。</summary>
internal sealed class ShutdownSequence(Action<string,Exception?> report)
{
    public int Failures {get;private set;}
    public void Run(string stage,Action work)
    {
        report(stage+"Starting",null);
        try{work();report(stage+"Completed",null);}
        catch(Exception error){Failures++;report(stage+"Failed",error);}
    }
    public async Task RunAsync(string stage,Func<Task> work)
    {
        report(stage+"Starting",null);
        try{await work();report(stage+"Completed",null);}
        catch(Exception error){Failures++;report(stage+"Failed",error);}
    }
}
