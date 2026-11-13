using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.Transactions;

/// <summary>一次本地扫描的精确相对路径及是否包含后代，不扩张到同级目录。</summary>
internal sealed record LocalUploadScanScope(string Path,bool Recursive);

/// <summary>不可变扫描范围；只对覆盖范围内的缺失项执行取消，保留范围外上传。</summary>
internal sealed class LocalUploadScanBatch
{
    internal static readonly LocalUploadScanBatch Full=new(true,[]);
    internal bool IsFull {get;}
    internal IReadOnlyList<LocalUploadScanScope> Scopes {get;}
    internal IReadOnlyList<LocalRenameNotice> Renames {get;}
    private readonly Dictionary<string,bool> _paths=new(StringComparer.Ordinal);
    internal LocalUploadScanBatch(bool full,IEnumerable<LocalUploadScanScope> scopes,IReadOnlyList<LocalRenameNotice>? renames=null)
    {
        IsFull=full;Renames=renames??[];
        var selected=new List<LocalUploadScanScope>();
        foreach(var scope in scopes.OrderBy(value=>value.Path.Count(character=>character=='/')).ThenBy(value=>value.Path.Length).ThenByDescending(value=>value.Recursive))
        {
            if(Covers(scope.Path))continue;
            _paths[scope.Path]=scope.Recursive;selected.Add(scope);
        }
        Scopes=selected;
    }
    internal bool Covers(string path)
    {
        if(IsFull||_paths.ContainsKey(path))return true;
        for(var index=path.LastIndexOf('/');index>=0;index=path.LastIndexOf('/',index-1))
        {
            if(_paths.TryGetValue(path[..index],out var recursive)&&recursive)return true;
            if(index==0)break;
        }
        return false;
    }
    internal IEnumerable<LocalUpload> MissingCandidates(IEnumerable<LocalUpload> uploads,ISet<string> seen)
        =>uploads.Where(item=>!item.IsCopy&&Covers(item.LocalPath)&&!seen.Contains(item.LocalPath)&&item.State is not("Completed" or "Canceled" or "Ignored"));
}

/// <summary>线程安全的Watcher去抖队列；合并路径、溢出全扫及失败补扫，并提供事件驱动消费者的下一截止。</summary>
internal sealed class LocalUploadScanQueue(int capacity=4096)
{
    private readonly object _sync=new();
    private readonly Dictionary<string,bool> _paths=new(StringComparer.Ordinal);
    private readonly List<LocalRenameNotice> _renames=[];
    private bool _full=true;
    private readonly Dictionary<string,bool> _changes=new(StringComparer.Ordinal);
    internal readonly LocalWriteStampCache Writes=new();
    private DateTimeOffset _fullAfter=DateTimeOffset.MinValue,_lastFull=DateTimeOffset.MinValue,_firstEvent,_lastEvent;
    internal void RequestFull(){lock(_sync){_full=true;_fullAfter=DateTimeOffset.MinValue;_paths.Clear();_changes.Clear();Writes.Clear();}}
    /// <summary>关闭只要求延后核对，给同一批改名因果留出交付窗口；连续关闭最多延后两秒。</summary>
    internal void RequestAfterClose(DateTimeOffset now)
    {
        lock(_sync)
        {
            if(!_full){_firstEvent=now;_full=true;}
            var quiet=now.AddMilliseconds(200);var bounded=_firstEvent.AddSeconds(2);
            _fullAfter=quiet<bounded?quiet:bounded;
        }
    }
    internal LocalUploadScanBatch StartFull(){lock(_sync){_full=false;_paths.Clear();_changes.Clear();Writes.Clear();var batch=new LocalUploadScanBatch(true,[],_renames.ToArray());_renames.Clear();return batch;}}
    /// <summary>原生完成通知只写有界内存；全扫和去抖不能丢弃已复制的改名关联。</summary>
    internal void RecordNativeRename(LocalRenameNotice notice){lock(_sync){if(_renames.Count<capacity)_renames.Add(notice);_full=true;_fullAfter=DateTimeOffset.MinValue;}}
    internal void Record(string root,FileSystemEventArgs change,DateTimeOffset now,IReadOnlyList<FileExclusionRule>? rules=null)
    {
        try
        {
            string Relative(string path)
            {
                if(!MountPathGuard.IsUnder(root,path))throw new ArgumentException("监控路径超出挂载根。");
                var relative=System.IO.Path.GetRelativePath(root,path);return relative=="."?"":RelativePaths.Normalize(relative.Replace('\\','/'));
            }
            var paths=change is RenamedEventArgs renamed?new[]{Relative(renamed.OldFullPath),Relative(renamed.FullPath)}:new[]{Relative(change.FullPath)};
            if(paths.Length==2&&paths[0]!=paths[1]&&paths[0].Equals(paths[1],StringComparison.OrdinalIgnoreCase)){RequestFull();return;}
            lock(_sync)
            {
                if(change is RenamedEventArgs&&paths.Length==2&&_renames.Count<capacity)_renames.Add(new(paths[0],paths[1],IntoExcluded:rules is null?null:!FileExclusionRules.Matches(rules,paths[0])&&FileExclusionRules.Matches(rules,paths[1])));
                if(_full)return;
                if(_paths.Count==0)_firstEvent=now;
                _lastEvent=now;
                foreach(var path in paths)
                {
                    if(path.Length==0){RequestFull();return;}
                    _paths[path]=true;
                    var eligible=change is not RenamedEventArgs&&change.ChangeType is WatcherChangeTypes.Changed or WatcherChangeTypes.Created;
                    _changes[path]=eligible&&(!_changes.TryGetValue(path,out var prior)||prior);
                    if(!eligible)Writes.Forget(path);
                    var parent=RelativePaths.Parent(path);_paths.TryAdd(parent,false);
                    if(_paths.Count>capacity){RequestFull();return;}
                }
            }
        }
        catch(Exception error)when(error is ArgumentException or IOException or NotSupportedException){RequestFull();}
    }
    /// <summary>返回下一去抖、失败补扫或六十秒兜底截止；不消费事件，避免单次通知后等待整分钟。</summary>
    internal DateTimeOffset NextDueUtc(DateTimeOffset now)
    {
        lock(_sync)
        {
            if(_full)return _fullAfter>now?_fullAfter:now;
            var due=_lastFull.AddSeconds(60);
            if(_paths.Count>0)
            {
                var quiet=_lastEvent.AddMilliseconds(200);var bounded=_firstEvent.AddSeconds(2);var changed=quiet<bounded?quiet:bounded;
                if(changed<due)due=changed;
            }
            return due>now?due:now;
        }
    }
    /// <summary>消费已到期的一批；可选属性读取只过滤同戳的文件自事件，删除、重命名及父目录范围保留，新事件进入下一批。</summary>
    internal LocalUploadScanBatch? Take(DateTimeOffset now,bool requested=false,Func<string,LocalFileStamp>? readStamp=null)
    {
        lock(_sync)
        {
            if(_full)return now>=_fullAfter?StartFull():null;
            if(now-_lastFull>=TimeSpan.FromSeconds(60)||requested&&_paths.Count==0)return StartFull();
            if(_paths.Count==0||now-_lastEvent<TimeSpan.FromMilliseconds(200)&&now-_firstEvent<TimeSpan.FromSeconds(2))return null;
            var scopes=_paths.Select(pair=>new LocalUploadScanScope(pair.Key,pair.Value)).ToList();
            if(readStamp is not null)scopes.RemoveAll(scope=>scope.Recursive&&_changes.GetValueOrDefault(scope.Path)&&Writes.Consume(scope.Path,now,()=>readStamp(scope.Path)));
            var batch=new LocalUploadScanBatch(false,scopes,_renames.ToArray());_renames.Clear();_paths.Clear();_changes.Clear();return batch;
        }
    }
    /// <summary>完整全扫重置兜底时钟；不完整扫描两秒后重试，完成通知不清除扫描期间新事件。</summary>
    internal void Completed(LocalUploadScanBatch batch,DateTimeOffset now,bool complete=true)
    {
        lock(_sync)
        {
            if(batch.IsFull)_lastFull=now;
            if(!complete){foreach(var rename in batch.Renames)if(_renames.Count<capacity&&!_renames.Contains(rename))_renames.Add(rename);_full=true;_fullAfter=now.AddSeconds(2);Writes.Clear();}
        }
    }
}
