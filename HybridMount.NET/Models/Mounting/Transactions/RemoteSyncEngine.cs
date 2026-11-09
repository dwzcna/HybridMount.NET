using HybridMount.Commons;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.Transactions;

/// <summary>只负责只读发现和持久事件；应用本地变更由根协调器执行。单实例调用串行，网络不持根锁。</summary>
internal sealed partial class RemoteSyncEngine(Guid mount,string scope,IFileSource source,IRemoteChangeSource changes,MountRepository repository,RemoteChangeSessionState? session=null)
{
    private readonly SemaphoreSlim _poll=new(1,1);
    public string Scope=>scope;
    public string Mode {get;private set;}="尚未检测";
    private readonly RemoteChangeSessionState _session=session??new();
    internal RemoteChangeFallback? Fallback=>_session.Fallback;
    public async Task PollAsync(bool forceFull,TimeSpan fullInterval,CancellationToken token)
    {
        await _poll.WaitAsync(token).ConfigureAwait(false);
        try
        {
            changes.RestoreObservations(repository.RemoteShadow(mount,scope));
            if(source is IRemoteSnapshotSource snapshot){await SnapshotAsync(snapshot,forceFull,fullInterval,token).ConfigureAwait(false);return;}
            try{await CollectionsAsync(forceFull,fullInterval,token).ConfigureAwait(false);}
            catch(InvalidRemoteChangeResponseException error)
            {
                token.ThrowIfCancellationRequested();_session.Disable(error);Mode=RemoteChangeSessionState.FallbackMode;
                // 失败页没有提交；从根强制完整抓取，不能只回退出错子集合或采用半份基线推断删除。
                await CollectionsAsync(true,fullInterval,token).ConfigureAwait(false);
            }
        }
        catch{_session.RequireFullScan();throw;}
        finally{_poll.Release();}
    }
    /// <summary>逐目录独立检测；当前会话的能力决定独立于持久检查点，重连可重新探测 Full 集合。</summary>
    private async Task CollectionsAsync(bool forceFull,TimeSpan fullInterval,CancellationToken token)
    {
        var disabled=_session.Fallback is not null;if(disabled){Mode=RemoteChangeSessionState.FallbackMode;forceFull|=_session.FullScanRequired;}
        var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var queue=new Queue<string>();queue.Enqueue("");
        while(queue.Count!=0)
        {
            token.ThrowIfCancellationRequested();var path=queue.Dequeue();if(!visited.Add(path))continue;
            if(visited.Count>1000000)throw new IOException("远端集合数量超过安全限制。");
            var checkpoint=repository.RemoteCollections(mount,scope).FirstOrDefault(x=>x.Path==path)??new(path);
            if(disabled)checkpoint=checkpoint with{Mode="Full"};
            else
            {
                if(!_session.TryGetMode(path,out var mode))
                {mode=await changes.SupportsCollectionSyncAsync(path,token).ConfigureAwait(false)?"RFC6578":"Full";_session.SetMode(path,mode);}
                if(mode=="RFC6578"&&checkpoint.Mode!="RFC6578")checkpoint=checkpoint with{Token=null,BuildingBaseline=false,Baseline=null};
                checkpoint=checkpoint with{Mode=mode};
            }
            if(forceFull)checkpoint=checkpoint with{Token=null,BuildingBaseline=false,Baseline=null};
            if(checkpoint.Mode=="RFC6578")
            {
                try
                {
                    try{await SyncAsync(checkpoint,token).ConfigureAwait(false);}
                    catch(RemoteChangeFailure error)when(error.InvalidCheckpoint)
                    {await SyncAsync(checkpoint with{Token=null,BuildingBaseline=false,Baseline=null},token).ConfigureAwait(false);}
                }
                catch(RemoteChangeFailure error)when(error.Unsupported)
                {_session.SetMode(path,"Full");await FullAsync(checkpoint with{Mode="Full",Token=null,BuildingBaseline=false,Baseline=null},token).ConfigureAwait(false);}
            }
            else if(forceFull||checkpoint.LastFullUtc is null||DateTimeOffset.UtcNow-checkpoint.LastFullUtc>=fullInterval)
                await FullAsync(checkpoint,token).ConfigureAwait(false);
            foreach(var child in repository.RemoteShadow(mount,scope).Where(x=>x.Entry.IsDirectory&&RelativePaths.Parent(x.Entry.Path)==path))queue.Enqueue(child.Entry.Path);
        }
        token.ThrowIfCancellationRequested();if(disabled)_session.CompleteFullScan();
        var modes=repository.RemoteCollections(mount,scope).Select(x=>x.Mode).Distinct().ToArray();
        Mode=disabled?RemoteChangeSessionState.FallbackMode:modes.Contains("RFC6578")?(modes.Contains("Full")?"RFC 6578 + 完整对账":"RFC 6578 增量检测"):"完整对账（服务器不支持 RFC 6578）";
    }
    /// <summary>一次扫描的所有分页先在锁外完成；缺失逐项查询当前状态，失败/取消不持久任何删除。</summary>
    private async Task SnapshotAsync(IRemoteSnapshotSource snapshot,bool force,TimeSpan interval,CancellationToken token)
    {
        for(var attempt=0;attempt<3;attempt++)
        {
            token.ThrowIfCancellationRequested();
            try{await SnapshotAttemptAsync(snapshot,force||attempt>0,interval,token).ConfigureAwait(false);return;}
            catch(SourceRescanRequiredException)when(attempt<2){await Task.Yield();}
        }
    }
    /// <summary>每次重扫从第一页开始；最多连续三轮，持续变化由后台短周期继续，不形成无限同步循环。</summary>
    private async Task SnapshotAttemptAsync(IRemoteSnapshotSource snapshot,bool force,TimeSpan interval,CancellationToken token)
    {
        Mode="S3 完整对账";var checkpoint=repository.RemoteCollections(mount,scope).FirstOrDefault(x=>x.Path=="")??new RemoteCollectionCheckpoint("",Mode:"Full");
        if(!force&&checkpoint.LastFullUtc is {} prior&&DateTimeOffset.UtcNow-prior<interval)return;
        var scan=Guid.NewGuid().ToString("N");repository.BeginRemoteScan(mount,scope,scan);
        try
        {
            var entries=(await snapshot.ReadSnapshotAsync(repository.RemoteShadow(mount,scope),token).ConfigureAwait(false)).ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase);
            var missing=repository.RemoteShadow(mount,scope).Select(x=>x.Entry.Path).Concat(repository.Entries(mount).Where(x=>x.RemotePresence!=RemotePresence.Deleted).Select(x=>x.SourcePath))
                .Where(x=>x.Length!=0&&!entries.ContainsKey(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var path in missing)
                if(await source.TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)throw new SourceRescanRequiredException("S3 列表缺失对象在当前查询仍存在，正在重新完整扫描，未确认删除。");
            token.ThrowIfCancellationRequested();
            Commit(checkpoint with{Mode="Full",LastFullUtc=DateTimeOffset.UtcNow},entries.Values.ToArray(),missing,false,scan);
        }
        catch{repository.FailRemoteScan(mount,scope,scan);throw;}
    }
    /// <summary>只消费协议适配器重新核对的提示结果；状态与检查点在现有事务中共同提交。</summary>
    public async Task ObserveEventHintsAsync(CancellationToken token)
    {
        if(source is not IRemoteHintSource hints)return;
        await foreach(var hint in hints.ReadHintChangesAsync(repository,mount,scope,token).ConfigureAwait(false))
        {
            var checkpoint=repository.RemoteCollections(mount,scope).FirstOrDefault(x=>x.Path=="")??new RemoteCollectionCheckpoint("",Mode:"Full");
            Commit(checkpoint,hint.Entries,hint.Deleted,false,hint:hint.Id);
        }
    }
    private async Task SyncAsync(RemoteCollectionCheckpoint checkpoint,CancellationToken token)
    {
        var initial=checkpoint.Token is null||checkpoint.BuildingBaseline;
        var accumulated=(checkpoint.Baseline??[]).ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase);
        var tokens=new HashSet<string>(StringComparer.Ordinal);if(checkpoint.Token is {} previous)tokens.Add(previous);
        for(var batchNumber=0;batchNumber<10000;batchNumber++)
        {
            var batch=await changes.GetCollectionChangesAsync(checkpoint.Path,checkpoint.Token,token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(string.IsNullOrWhiteSpace(batch.NextToken)||batch.NextToken.Length>65536||batch.HasMore&&!tokens.Add(batch.NextToken))
                throw new InvalidRemoteChangeResponseException("远端续批令牌缺失、超限或重复，未推进本页检查点。");
            if(initial)
            {
                foreach(var entry in batch.Entries)
                {if(accumulated.ContainsKey(entry.Path))throw new InvalidRemoteChangeResponseException("DAV 初始续批包含重复路径，未提交完整基线。");accumulated.Add(entry.Path,entry);}
                if(batch.Deleted.Count!=0)throw new InvalidRemoteChangeResponseException("DAV 初始续批包含删除，未提交完整基线。");
                if(accumulated.Count>1000000)throw new InvalidRemoteChangeResponseException("DAV 初始基线超过安全限制。");
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
        throw new InvalidRemoteChangeResponseException("DAV 续批次数超过安全限制，保留检查点待重试。");
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
    private void Commit(RemoteCollectionCheckpoint checkpoint,IReadOnlyList<SourceEntry> incoming,IReadOnlyList<string> deleted,bool explicitChanges,string? scan=null,string? hint=null)
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
        repository.CommitRemoteBatch(mount,scope,checkpoint,changed,deleted,silent,scan,hint);
    }
}
