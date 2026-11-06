using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

internal sealed record LocalUploadScanScope(string Path,bool Recursive);

internal sealed class LocalUploadScanBatch
{
    internal static readonly LocalUploadScanBatch Full=new(true,[]);
    internal bool IsFull {get;}
    internal IReadOnlyList<LocalUploadScanScope> Scopes {get;}
    private readonly Dictionary<string,bool> _paths=new(StringComparer.Ordinal);
    internal LocalUploadScanBatch(bool full,IEnumerable<LocalUploadScanScope> scopes)
    {
        IsFull=full;
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

internal sealed class LocalUploadScanQueue(int capacity=4096)
{
    private readonly object _sync=new();
    private readonly Dictionary<string,bool> _paths=new(StringComparer.Ordinal);
    private bool _full=true;
    private DateTimeOffset _fullAfter=DateTimeOffset.MinValue,_lastFull=DateTimeOffset.MinValue,_firstEvent,_lastEvent;
    internal void RequestFull(){lock(_sync){_full=true;_fullAfter=DateTimeOffset.MinValue;_paths.Clear();}}
    internal LocalUploadScanBatch StartFull(){lock(_sync){_full=false;_paths.Clear();return LocalUploadScanBatch.Full;}}
    internal void Record(string root,FileSystemEventArgs change,DateTimeOffset now)
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
                if(_full)return;
                if(_paths.Count==0)_firstEvent=now;
                _lastEvent=now;
                foreach(var path in paths)
                {
                    if(path.Length==0){RequestFull();return;}
                    _paths[path]=true;
                    var parent=RelativePaths.Parent(path);_paths.TryAdd(parent,false);
                    if(_paths.Count>capacity){RequestFull();return;}
                }
            }
        }
        catch(Exception error)when(error is ArgumentException or IOException or NotSupportedException){RequestFull();}
    }
    internal LocalUploadScanBatch? Take(DateTimeOffset now,bool requested=false)
    {
        lock(_sync)
        {
            if(_full)return now>=_fullAfter?StartFull():null;
            if(now-_lastFull>=TimeSpan.FromSeconds(60)||requested&&_paths.Count==0)return StartFull();
            if(_paths.Count==0||now-_lastEvent<TimeSpan.FromMilliseconds(200)&&now-_firstEvent<TimeSpan.FromSeconds(2))return null;
            var batch=new LocalUploadScanBatch(false,_paths.Select(pair=>new LocalUploadScanScope(pair.Key,pair.Value)));_paths.Clear();return batch;
        }
    }
    internal void Completed(LocalUploadScanBatch batch,DateTimeOffset now,bool complete=true)
    {
        lock(_sync)
        {
            if(batch.IsFull)_lastFull=now;
            if(!complete){_full=true;_fullAfter=now.AddSeconds(2);}
        }
    }
}
