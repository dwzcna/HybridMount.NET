using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>只负责只读发现和持久事件；应用本地变更由根协调器执行。单实例调用串行，网络不持根锁。</summary>
internal sealed class RemoteSyncEngine(Guid mount,string scope,IFileSource source,IRemoteChangeSource changes,MountRepository repository)
{
    private readonly SemaphoreSlim _poll=new(1,1);
    public string Scope=>scope;
    public string Mode {get;private set;}="尚未检测";
    public async Task PollAsync(bool forceFull,TimeSpan fullInterval,CancellationToken token)
    {
        await _poll.WaitAsync(token).ConfigureAwait(false);
        try
        {
            changes.RestoreObservations(repository.RemoteShadow(mount,scope));
            var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var queue=new Queue<string>();queue.Enqueue("");
            while(queue.Count!=0)
            {
                token.ThrowIfCancellationRequested();var path=queue.Dequeue();if(!visited.Add(path))continue;
                if(visited.Count>1000000)throw new IOException("远端集合数量超过安全限制。");
                var checkpoint=repository.RemoteCollections(mount,scope).FirstOrDefault(x=>x.Path==path)??new(path);
                if(checkpoint.Mode=="Unknown")
                {
                    checkpoint=checkpoint with{Mode=await changes.SupportsCollectionSyncAsync(path,token).ConfigureAwait(false)?"RFC6578":"Full"};
                }
                if(forceFull)checkpoint=checkpoint with{Token=null,BuildingBaseline=false,Baseline=null};
                if(checkpoint.Mode=="RFC6578")
                {
                    try
                    {
                        try{await SyncAsync(checkpoint,token).ConfigureAwait(false);}
                        catch(RemoteSyncException error)when(error.InvalidToken)
                        {await SyncAsync(checkpoint with{Token=null,BuildingBaseline=false,Baseline=null},token).ConfigureAwait(false);}
                    }
                    catch(RemoteSyncException error)when(error.Unsupported)
                    { await FullAsync(checkpoint with{Mode="Full",Token=null,BuildingBaseline=false,Baseline=null},token).ConfigureAwait(false); }
                }
                else if(forceFull||checkpoint.LastFullUtc is null||DateTimeOffset.UtcNow-checkpoint.LastFullUtc>=fullInterval)
                    await FullAsync(checkpoint,token).ConfigureAwait(false);
                // 子目录分别拥有令牌；不认为父集合报告覆盖所有后代。
                foreach(var child in repository.RemoteShadow(mount,scope).Where(x=>x.Entry.IsDirectory&&RelativePaths.Parent(x.Entry.Path)==path))queue.Enqueue(child.Entry.Path);
            }
            var modes=repository.RemoteCollections(mount,scope).Select(x=>x.Mode).Distinct().ToArray();
            Mode=modes.Contains("RFC6578")?(modes.Contains("Full")?"RFC 6578 + 完整对账":"RFC 6578 增量检测"):"完整对账（服务器不支持 RFC 6578）";
        }
        finally{_poll.Release();}
    }
    private async Task SyncAsync(RemoteCollectionCheckpoint checkpoint,CancellationToken token)
    {
        var initial=checkpoint.Token is null||checkpoint.BuildingBaseline;
        var accumulated=(checkpoint.Baseline??[]).ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase);
        for(var batchNumber=0;batchNumber<10000;batchNumber++)
        {
            var batch=await changes.GetCollectionChangesAsync(checkpoint.Path,checkpoint.Token,token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(initial)
            {
                foreach(var entry in batch.Entries)
                {if(accumulated.ContainsKey(entry.Path))throw new IOException("DAV 初始续批包含重复路径，未提交完整基线。");accumulated.Add(entry.Path,entry);}
                if(batch.Deleted.Count!=0)throw new IOException("DAV 初始续批包含删除，未提交完整基线。");
                if(accumulated.Count>1000000)throw new IOException("DAV 初始基线超过安全限制。");
                checkpoint=checkpoint with{Token=batch.NextToken,BuildingBaseline=batch.HasMore,Baseline=batch.HasMore?accumulated.Values.ToArray():null};
                if(batch.HasMore)repository.CommitRemoteBatch(mount,scope,checkpoint,[],[]);
                else
                {
                    var missing=await ConfirmMissingAsync(Missing(checkpoint.Path,accumulated.Keys),accumulated,token).ConfigureAwait(false);
                    Commit(checkpoint,accumulated.Values.ToArray(),missing,false);
                }
            }
            else
            {
                checkpoint=checkpoint with{Token=batch.NextToken};Commit(checkpoint,batch.Entries,batch.Deleted,true);
            }
            if(!batch.HasMore)return;
        }
        throw new IOException("DAV 续批次数超过安全限制，保留检查点待重试。");
    }
    private async Task FullAsync(RemoteCollectionCheckpoint checkpoint,CancellationToken token)
    {
        var entries=new Dictionary<string,SourceEntry>(StringComparer.OrdinalIgnoreCase);
        await foreach(var entry in source.ListAsync(checkpoint.Path,token).ConfigureAwait(false))
        {
            if(entry.Path.Length==0||RelativePaths.Parent(entry.Path)!=checkpoint.Path||entry.Size<0||!entries.TryAdd(entry.Path,entry))throw new IOException("DAV 完整对账含越界、重复或无效条目。");
            if(entries.Count>1000000)throw new IOException("DAV 完整对账超过安全限制。");
        }
        token.ThrowIfCancellationRequested();
        var missing=await ConfirmMissingAsync(Missing(checkpoint.Path,entries.Keys),entries,token).ConfigureAwait(false);
        Commit(checkpoint with{Mode="Full",Token=null,BuildingBaseline=false,Baseline=null,LastFullUtc=DateTimeOffset.UtcNow},entries.Values.ToArray(),missing,false);
    }
    private async Task<IReadOnlyList<string>> ConfirmMissingAsync(IReadOnlyList<string> candidates,Dictionary<string,SourceEntry> current,CancellationToken token)
    {
        var missing=new List<string>();
        foreach(var path in candidates)
        {
            var metadata=await source.TryGetMetadataAsync(path,token).ConfigureAwait(false);
            if(metadata is null)missing.Add(path);else current[path]=metadata;
        }
        return missing;
    }
    private IReadOnlyList<string> Missing(string collection,IEnumerable<string> current)
    {
        var seen=current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return repository.RemoteShadow(mount,scope).Select(x=>x.Entry.Path).Concat(repository.Entries(mount).Where(x=>x.RemotePresence!=RemotePresence.Deleted).Select(x=>x.SourcePath))
            .Where(x=>x.Length!=0&&RelativePaths.Parent(x)==collection&&!seen.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private void Commit(RemoteCollectionCheckpoint checkpoint,IReadOnlyList<SourceEntry> incoming,IReadOnlyList<string> deleted,bool explicitChanges)
    {
        var shadow=repository.RemoteShadow(mount,scope).ToDictionary(x=>x.Entry.Path,StringComparer.OrdinalIgnoreCase);var changed=new List<RemoteShadowEntry>();var silent=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in incoming)
        {
            shadow.TryGetValue(entry.Path,out var old);
            if(old is not null&&old.Entry.Path!=entry.Path)throw new IOException("远端路径大小写变化需要核对，本集合检查点未推进。");
            var same=old is not null&&source.IsSameVersion(old.Entry.Version,entry.Version)&&old.Entry.IsDirectory==entry.IsDirectory;
            if(same&&(!explicitChanges||changes.HasReliableVersion(entry)))continue;
            var observation=(old?.Observation??0)+(explicitChanges&&!changes.HasReliableVersion(entry)?1:0);
            changed.Add(new(changes.WithObservation(entry,observation),observation));
            if(old is null&&repository.Entries(mount).Any(x=>x.SourcePath==entry.Path&&x.IsDirectory==entry.IsDirectory&&x.Materialized&&x.RemotePresence!=RemotePresence.Deleted&&source.IsSameVersion(x.Version,entry.Version)))silent.Add(entry.Path);
        }
        repository.CommitRemoteBatch(mount,scope,checkpoint,changed,deleted,silent);
    }
}
