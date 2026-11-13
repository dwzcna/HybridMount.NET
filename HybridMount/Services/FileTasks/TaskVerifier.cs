namespace HybridMount.Services.FileTasks;

/// <summary>只读核验已有回执和实际业务状态；不重发写入，未知结果不会放行后续步骤。</summary>
internal sealed class TaskVerifier
{
    internal async Task<TaskVerification> VerifyAsync(Func<CancellationToken,Task<TaskVerification>> verify,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try{return await verify(deadline.Token).ConfigureAwait(false);}
        catch(Exception error){return new("OutcomeUnknown","verification",error.HResult);}
    }
}
