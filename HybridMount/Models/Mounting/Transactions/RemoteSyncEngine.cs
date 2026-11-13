using HybridMount.Commons;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.Transactions;

/// <summary>只负责只读发现和持久事件；应用本地变更由根协调器执行。单实例调用串行，网络不持根锁。</summary>
internal sealed partial class RemoteSyncEngine(Guid mount,string scope,IFileSource source,IRemoteChangeSource changes,MountRepository repository,RemoteChangeSessionState? session=null)
{
    private readonly SemaphoreSlim _poll=new(1,1);
    public string Scope=>scope;
    internal const int PublicationBatchSize=256;
    private long _discovered;
    /// <summary>只在发现事务提交后发布新占位；网络枚举和数据库事务均不跨此回调。</summary>
    internal Func<IReadOnlyList<RemotePendingChange>,CancellationToken,Task<IReadOnlyList<MountEntry>>>? PublishDiscovered {get;set;}
    internal Action<long>? DiscoveryProgress {get;set;}
    private async Task PublishAsync(IReadOnlyList<RemotePendingChange> pending,CancellationToken token)
    {
        if(PublishDiscovered is null||pending.Count==0)return;
        foreach(var batch in pending.Chunk(PublicationBatchSize))foreach(var entry in await PublishDiscovered(batch,token).ConfigureAwait(false))_index?.Published(entry);
    }
    private void Discovered(int count){_discovered+=count;DiscoveryProgress?.Invoke(_discovered);}
    public string Mode {get;private set;}="尚未检测";
    private readonly RemoteChangeSessionState _session=session??new();
    internal RemoteChangeFallback? Fallback=>_session.Fallback;
    public async Task PollAsync(bool forceFull,TimeSpan fullInterval,CancellationToken token)
    {
        await _poll.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _discovered=0;_index=LoadIndex();changes.RestoreObservations(_index.Shadow.Values);
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
        finally{_index=null;_poll.Release();}
    }
    /// <summary>逐目录独立检测；当前会话的能力决定独立于持久检查点，重连可重新探测 Full 集合。</summary>
    private async Task CollectionsAsync(bool forceFull,TimeSpan fullInterval,CancellationToken token)
    {
        var disabled=_session.Fallback is not null;forceFull|=_session.FullScanRequired;if(disabled)Mode=RemoteChangeSessionState.FallbackMode;
        var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var queue=new Queue<string>();queue.Enqueue("");
        while(queue.Count!=0)
        {
            token.ThrowIfCancellationRequested();var path=queue.Dequeue();if(!visited.Add(path))continue;
            if(visited.Count>1000000)throw new IOException("远端集合数量超过安全限制。");
            var checkpoint=_index!.Collections.GetValueOrDefault(path)??new(path);
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
            foreach(var child in _index!.Directories(path).ToArray())queue.Enqueue(child);
        }
        token.ThrowIfCancellationRequested();_session.CompleteFullScan();
        var modes=_index!.Collections.Values.Select(x=>x.Mode).Distinct().ToArray();
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
        Mode="S3 完整对账";var checkpoint=_index!.Collections.GetValueOrDefault("")??new RemoteCollectionCheckpoint("",Mode:"Full");
        if(!force&&checkpoint.LastFullUtc is {} prior&&DateTimeOffset.UtcNow-prior<interval)return;
        var scan=Guid.NewGuid().ToString("N");repository.BeginRemoteScan(mount,scope,scan);
        var building=checkpoint with{Mode="Full",Token=null,BuildingBaseline=true,Baseline=null,LastFullUtc=null};
        try
        {
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var batch=new List<SourceEntry>(PublicationBatchSize);
            async Task Flush(){if(batch.Count==0)return;token.ThrowIfCancellationRequested();Discovered(batch.Count);var pending=Commit(building,batch.ToArray(),[],false);batch.Clear();await PublishAsync(pending,token).ConfigureAwait(false);}
            async Task Observe(SourceEntry entry){RelativePaths.Normalize(entry.Path);if(entry.Path.Length==0||entry.Size<0||!seen.Add(entry.Path))throw new IOException("完整快照含重复、越界或无效条目。");if(seen.Count>1000000)throw new IOException("完整快照超过安全限制。");batch.Add(entry);if(batch.Count==PublicationBatchSize)await Flush().ConfigureAwait(false);}
            var previous=_index.Shadow.Values.ToArray();
            if(snapshot is IStreamingRemoteSnapshotSource streaming){await foreach(var entry in streaming.ReadSnapshotStreamAsync(previous,token).ConfigureAwait(false))await Observe(entry).ConfigureAwait(false);}
            else foreach(var entry in (await snapshot.ReadSnapshotAsync(previous,token).ConfigureAwait(false)).OrderBy(entry=>entry.Path.Count(c=>c=='/')).ThenBy(entry=>entry.Path,StringComparer.Ordinal))await Observe(entry).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();await Flush().ConfigureAwait(false);
            var missing=_index.Shadow.Keys.Concat(_index.Local.Values.Where(item=>item.RemotePresence!=RemotePresence.Deleted).Select(item=>item.SourcePath)).Where(path=>path.Length!=0&&!seen.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var path in missing)if(await source.TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)throw new SourceRescanRequiredException("S3 列表缺失对象在当前查询仍存在，正在重新完整扫描，未确认删除。");
            token.ThrowIfCancellationRequested();foreach(var deleted in missing.Chunk(PublicationBatchSize))Commit(building,[],deleted,false);
            Commit(checkpoint with{Mode="Full",Token=null,BuildingBaseline=false,Baseline=null,LastFullUtc=DateTimeOffset.UtcNow},[],[],false,scan);
        }
        catch{repository.FailRemoteScan(mount,scope,scan);throw;}
    }
    /// <summary>只消费协议适配器重新核对的提示结果；状态与检查点在现有事务中共同提交。</summary>
    public async Task ObserveEventHintsAsync(CancellationToken token)
    {
        if(source is not IRemoteHintSource hints)return;
        await foreach(var hint in hints.ReadHintChangesAsync(repository,mount,scope,token).ConfigureAwait(false))
        {
            var checkpoint=(_index??LoadIndex()).Collections.GetValueOrDefault("")??new RemoteCollectionCheckpoint("",Mode:"Full");
            Commit(checkpoint,hint.Entries,hint.Deleted,false,hint:hint.Id);
        }
    }
    private async Task SyncAsync(RemoteCollectionCheckpoint checkpoint,CancellationToken token)
    {
        if(checkpoint.BuildingBaseline&&checkpoint.Baseline is null)checkpoint=checkpoint with{Token=null,BuildingBaseline=false};
        var initial=checkpoint.Token is null||checkpoint.BuildingBaseline;
        var seen=(checkpoint.Baseline??[]).Select(entry=>entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokens=new HashSet<string>(StringComparer.Ordinal);if(checkpoint.Token is {} previous)tokens.Add(previous);
        for(var batchNumber=0;batchNumber<10000;batchNumber++)
        {
            var batch=await changes.GetCollectionChangesAsync(checkpoint.Path,checkpoint.Token,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if(string.IsNullOrWhiteSpace(batch.NextToken)||batch.NextToken.Length>65536||batch.HasMore&&!tokens.Add(batch.NextToken))throw new InvalidRemoteChangeResponseException("远端续批令牌缺失、超限或重复，未推进本页检查点。");
            if(initial)
            {
                if(batch.Deleted.Count!=0)throw new InvalidRemoteChangeResponseException("DAV 初始续批包含删除，未提交完整基线。");
                foreach(var entry in batch.Entries){if(!seen.Add(entry.Path))throw new InvalidRemoteChangeResponseException("DAV 初始续批包含重复路径，未完成基线。");if(seen.Count>1000000)throw new InvalidRemoteChangeResponseException("DAV 初始基线超过安全限制。");}
                // 部分发现保留Full模式；新旧程序重开时均从根去重重扫，不把未完成页面当成完整基线。
                var building=checkpoint with{Mode="Full",Token=batch.NextToken,BuildingBaseline=true,Baseline=null,LastFullUtc=null};Mode="RFC 6578 初始对账";
                foreach(var page in batch.Entries.Chunk(PublicationBatchSize)){Discovered(page.Length);await PublishAsync(Commit(building,page,[],false),token).ConfigureAwait(false);}
                if(batch.Entries.Count==0)Commit(building,[],[],false);
                checkpoint=building;
                if(!batch.HasMore)
                {
                    var recovered=new Dictionary<string,SourceEntry>(StringComparer.OrdinalIgnoreCase);var missing=await ConfirmMissingAsync(Missing(checkpoint.Path,seen),recovered,token).ConfigureAwait(false);
                    foreach(var page in recovered.Values.Chunk(PublicationBatchSize))await PublishAsync(Commit(building,page,[],false),token).ConfigureAwait(false);
                    foreach(var deleted in missing.Chunk(PublicationBatchSize))Commit(building,[],deleted,false);
                    Commit(checkpoint with{Mode="RFC6578",BuildingBaseline=false,Baseline=null},[],[],false);
                }
            }
            else
            {
                checkpoint=checkpoint with{Token=batch.NextToken};await PublishAsync(Commit(checkpoint,batch.Entries,batch.Deleted,true),token).ConfigureAwait(false);
            }
            if(!batch.HasMore)return;
        }
        throw new InvalidRemoteChangeResponseException("DAV 续批次数超过安全限制，保留检查点待重试。");
    }
    private async Task FullAsync(RemoteCollectionCheckpoint checkpoint,CancellationToken token)
    {
        Mode=_session.Fallback is null?"完整对账":RemoteChangeSessionState.FallbackMode;
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var batch=new List<SourceEntry>(PublicationBatchSize);
        var building=checkpoint with{Mode="Full",Token=null,BuildingBaseline=true,Baseline=null,LastFullUtc=null};
        async Task Flush()
        {
            if(batch.Count==0)return;token.ThrowIfCancellationRequested();Discovered(batch.Count);
            var pending=Commit(building,batch.ToArray(),[],false);batch.Clear();await PublishAsync(pending,token).ConfigureAwait(false);
        }
        await foreach(var entry in source.ListAsync(checkpoint.Path,token).ConfigureAwait(false))
        {
            if(entry.Path.Length==0||RelativePaths.Parent(entry.Path)!=checkpoint.Path||entry.Size<0||!seen.Add(entry.Path))throw new IOException("完整对账含越界、重复或无效条目。");
            if(seen.Count>1000000)throw new IOException("完整对账超过安全限制。");batch.Add(entry);if(batch.Count==PublicationBatchSize)await Flush().ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();await Flush().ConfigureAwait(false);
        var confirmed=new Dictionary<string,SourceEntry>(StringComparer.OrdinalIgnoreCase);
        var missing=await ConfirmMissingAsync(Missing(checkpoint.Path,seen),confirmed,token).ConfigureAwait(false);
        foreach(var recovered in confirmed.Values.Chunk(PublicationBatchSize))await PublishAsync(Commit(building,recovered,[],false),token).ConfigureAwait(false);
        foreach(var deleted in missing.Chunk(PublicationBatchSize))Commit(building,[],deleted,false);
        Commit(checkpoint with{Mode="Full",Token=null,BuildingBaseline=false,Baseline=null,LastFullUtc=DateTimeOffset.UtcNow},[],[],false);
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
        return _index!.KnownChildren(collection).Where(x=>x.Length!=0&&!seen.Contains(x)).ToArray();
    }
    private IReadOnlyList<RemotePendingChange> Commit(RemoteCollectionCheckpoint checkpoint,IReadOnlyList<SourceEntry> incoming,IReadOnlyList<string> deleted,bool explicitChanges,string? scan=null,string? hint=null)
    {
        var index=_index??LoadIndex();var shadow=index.Shadow;var changed=new List<RemoteShadowEntry>();var silent=new HashSet<string>(StringComparer.Ordinal);
        var repeated=new Dictionary<string,RemoteShadowEntry>(StringComparer.Ordinal);
        foreach(var entry in incoming)
        {
            shadow.TryGetValue(entry.Path,out var old);
            if(old is not null&&old.Entry.Path!=entry.Path)throw new IOException("远端路径大小写变化需要核对，本集合检查点未推进。");
            // 完整扫描的相同观测可以去重；明确增量的弱版本仍推进代次，不能用元数据证明正文未变。
            var observation=(old?.Observation??0)+(explicitChanges&&!changes.HasReliableVersion(entry)?1:0);
            var observed=changes.WithObservation(entry,observation);
            var same=old is not null&&old.Entry.IsDirectory==entry.IsDirectory&&source.IsSameVersion(old.Entry.Version,observed.Version);
            if(same&&(!explicitChanges||changes.HasReliableVersion(entry)))repeated[entry.Path]=old!;
            changed.Add(new(observed,observation));
            if(old is null&&(entry.IsDirectory||changes.HasReliableVersion(entry))&&index.Local.TryGetValue(entry.Path,out var local)&&local.SourcePath==entry.Path&&local.IsDirectory==entry.IsDirectory&&local.Materialized&&local.RemotePresence!=RemotePresence.Deleted&&source.IsSameVersion(local.Version,entry.Version))silent.Add(entry.Path);
        }
        var pending=repository.CommitDiscoveredBatch(mount,scope,checkpoint,changed,deleted,silent,scan,hint,repeated);index.Committed(checkpoint,changed,deleted);return pending;
    }
}
