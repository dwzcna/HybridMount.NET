using HybridMount.Helpers.Paths;
using HybridMount.Models.Mounting;
using System.Diagnostics;
using System.Text.Json;
namespace HybridMount.Models.Mounting.Tasks;

/// <summary>一次任务页读取的不可变业务视图；仅用于展示，执行授权必须重新点查。</summary>
internal sealed class TaskStateSnapshot
{
    internal required IReadOnlyList<MountOperationSnapshot> History {get;init;}
    internal required IReadOnlyList<LocalUpload> Uploads {get;init;}
    internal required IReadOnlyList<RemotePendingChange> Remote {get;init;}
    internal required IReadOnlyList<(Guid Mount,MountEntry Entry)> Deleted {get;init;}
    internal required IReadOnlySet<string> ProtocolBlockedUploads {get;init;}
    internal IReadOnlyList<MountOperationSnapshot> RemoteBatches {get;init;}=[];
    internal IReadOnlySet<string> BatchedRemoteIds {get;init;}=new HashSet<string>(StringComparer.Ordinal);
    internal readonly Dictionary<string,LocalUpload> ById=new(StringComparer.Ordinal);
    private readonly Dictionary<Guid,Dictionary<string,List<RemotePendingChange>>> _remote=[];
    private readonly Dictionary<Guid,Dictionary<string,MountEntry>> _deleted=[];
    internal void Index()
    {
        foreach(var item in Uploads)ById[item.Id]=item;
        foreach(var item in Remote.Where(RemotePendingRules.BlocksLocalPath))
        {
            if(!_remote.TryGetValue(item.MountId,out var paths))_remote[item.MountId]=paths=new(StringComparer.OrdinalIgnoreCase);
            if(!paths.TryGetValue(item.Path,out var values))paths[item.Path]=values=[];values.Add(item);
        }
        foreach(var (mount,entry) in Deleted)
        {if(!_deleted.TryGetValue(mount,out var paths))_deleted[mount]=paths=new(StringComparer.OrdinalIgnoreCase);paths[entry.SourcePath]=entry;}
    }
    internal static IEnumerable<string> Ancestors(string path)
    {while(true){yield return path;if(path.Length==0)yield break;path=RelativePaths.Parent(path);}}
    internal RemotePendingChange? Blocker(Guid mount,string path,string id)
    {
        if(_remote.TryGetValue(mount,out var paths))foreach(var ancestor in Ancestors(path))
            if(paths.TryGetValue(ancestor,out var values)&&values.FirstOrDefault(x=>x.Id!=id) is {} blocker)return blocker;
        return null;
    }
    internal MountEntry? DeletedAncestor(Guid mount,string path)
    {if(_deleted.TryGetValue(mount,out var paths))foreach(var ancestor in Ancestors(path))if(paths.TryGetValue(ancestor,out var entry))return entry;return null;}
}
