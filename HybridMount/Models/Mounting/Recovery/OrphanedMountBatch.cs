using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.Recovery;

/// <summary>固定所选身份后逐根串行处理；失败不中断其他根，取消只阻止剩余操作。</summary>
internal static class OrphanedMountBatch
{
    /// <summary>规范路径去重并拒绝不可处理项、冲突身份和包含关系；只检查快照，不访问文件正文。</summary>
    internal static OrphanedMountCandidate[] Capture(IEnumerable<OrphanedMountCandidate> roots)
    {
        var selected=new List<OrphanedMountCandidate>();
        foreach(var root in roots)
        {
            if(!root.CanRemove)throw new IOException("所选项目未通过核验："+root.LocalPath);
            var path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.LocalPath));
            var previous=selected.FirstOrDefault(x=>x.LocalPath.Equals(path,StringComparison.OrdinalIgnoreCase));
            if(previous is not null){if((previous with{LocalPath=path})!=(root with{LocalPath=path}))throw new IOException("同目录出现不同身份，请重新核对。");continue;}
            if(selected.Any(x=>MountPathGuard.IsUnder(x.LocalPath,path)||MountPathGuard.IsUnder(path,x.LocalPath)))throw new IOException("不能同时处理互相包含的目录。");
            selected.Add(root with{LocalPath=path});
        }
        if(selected.Count==0)throw new InvalidOperationException("请先选择可处理的遗留挂载。");
        return selected.ToArray();
    }
    /// <summary>调用方持有应用生命周期并在执行器中逐根加锁；进度回调应自行调度到UI线程。</summary>
    internal static async Task<OrphanedMountBatchResult> RunAsync(IReadOnlyList<OrphanedMountCandidate> roots,
        Func<OrphanedMountCandidate,CancellationToken,Action<string>,Task<OrphanedMountResult>> execute,CancellationToken token,
        Action<OrphanedMountBatchProgress>? progress=null)
    {
        var results=new List<OrphanedMountItemResult>();
        for(var index=0;index<roots.Count;index++)
        {
            var root=roots[index];
            if(token.IsCancellationRequested){results.Add(new(root,OrphanedMountItemState.NotStarted,0,"NotStarted",null,[]));continue;}
            var stage="核验";
            void Report(string line){stage=line;progress?.Invoke(new(index+1,roots.Count,root.LocalPath,line));}
            try
            {
                Report("正在重新核对目录身份…");
                var result=await execute(root,token,Report).ConfigureAwait(false);
                results.Add(new(root,result.Completed?OrphanedMountItemState.Completed:OrphanedMountItemState.Failed,result.Deleted,result.Stage,result.HResult,result.Errors));
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested)
            {results.Add(new(root,OrphanedMountItemState.Canceled,0,stage,unchecked((int)0x800703e3),["已停止；已完成的删除不回滚。"]));}
            catch(Exception error)
            {results.Add(new(root,OrphanedMountItemState.Failed,0,stage,Code(error),[error.Message]));}
        }
        return new(results);
    }
    internal static int Code(Exception error)=>error is System.ComponentModel.Win32Exception native?unchecked((int)(0x80070000u|(uint)native.NativeErrorCode)):error.HResult;
}
