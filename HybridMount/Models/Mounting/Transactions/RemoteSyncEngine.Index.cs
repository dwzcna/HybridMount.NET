namespace HybridMount.Models.Mounting.Transactions;

internal sealed partial class RemoteSyncEngine
{
    private ScanIndex? _index;
    /// <summary>一次轮询仅加载一份本地、远端及目录索引；路径与稳定ID字典避免逐文件扫描整个挂载。</summary>
    private sealed class ScanIndex
    {
        internal readonly Dictionary<string,MountEntry> Local=new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,MountEntry> LocalIds=new(StringComparer.Ordinal);
        internal readonly Dictionary<string,RemoteShadowEntry> Shadow=new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,RemoteCollectionCheckpoint> Collections=new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,HashSet<string>> _known=new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,HashSet<string>> _directories=new(StringComparer.OrdinalIgnoreCase);
        internal ScanIndex(IReadOnlyList<MountEntry> local,IReadOnlyList<RemoteShadowEntry> shadow,IReadOnlyList<RemoteCollectionCheckpoint> collections)
        {
            foreach(var item in local){if(!Local.TryAdd(item.SourcePath,item)||!LocalIds.TryAdd(item.Id,item))throw new IOException("本地索引含重复路径或身份，保留现场等待核对。");if(item.RemotePresence!=RemotePresence.Deleted)Known(item.SourcePath);}
            foreach(var item in shadow){if(!Shadow.TryAdd(item.Entry.Path,item))throw new IOException("远端索引含大小写冲突，保留现场等待核对。");Known(item.Entry.Path);if(item.Entry.IsDirectory)Directory(item.Entry.Path);}
            foreach(var item in collections)Collections[item.Path]=item;
        }
        internal void Published(MountEntry entry){Local[entry.SourcePath]=entry;LocalIds[entry.Id]=entry;Known(entry.SourcePath);}
        private void Known(string path){var parent=RelativePaths.Parent(path);if(!_known.TryGetValue(parent,out var items))_known[parent]=items=[];items.Add(path);}
        private void Directory(string path){var parent=RelativePaths.Parent(path);if(!_directories.TryGetValue(parent,out var items))_directories[parent]=items=[];items.Add(path);}
        internal IEnumerable<string> KnownChildren(string path)=>_known.GetValueOrDefault(path)??Enumerable.Empty<string>();
        internal IEnumerable<string> Directories(string path)=>_directories.GetValueOrDefault(path)??Enumerable.Empty<string>();
        /// <summary>仅在数据库提交成功后推进内存视图，不让失败页影响后续目录和删除判定。</summary>
        internal void Committed(RemoteCollectionCheckpoint checkpoint,IReadOnlyList<RemoteShadowEntry> changed,IReadOnlyList<string> deleted)
        {
            foreach(var path in deleted)Remove(path);
            foreach(var item in changed)
            {
                var path=item.Entry.Path;if(Shadow.TryGetValue(path,out var previous)&&previous.Entry.IsDirectory&&!item.Entry.IsDirectory)Remove(path);
                Shadow[path]=item;Known(path);if(item.Entry.IsDirectory)Directory(path);
            }
            Collections[checkpoint.Path]=checkpoint;
        }
        private void Remove(string path)
        {
            var pending=new Stack<string>();pending.Push(path);
            while(pending.TryPop(out var item))
            {
                if(_known.TryGetValue(item,out var children))foreach(var child in children.ToArray())if(child!=item)pending.Push(child);
                Shadow.Remove(item);Collections.Remove(item);
                if(_directories.TryGetValue(RelativePaths.Parent(item),out var dirs))dirs.Remove(item);
                if(!Local.TryGetValue(item,out var local)||local.RemotePresence==RemotePresence.Deleted)if(_known.TryGetValue(RelativePaths.Parent(item),out var known))known.Remove(item);
            }
        }
    }
    private ScanIndex LoadIndex()=>new(repository.Entries(mount),repository.RemoteShadow(mount,scope),repository.RemoteCollections(mount,scope));
}
