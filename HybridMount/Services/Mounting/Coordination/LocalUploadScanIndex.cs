using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

internal sealed class LocalUploadScanIndex
{
    private readonly Dictionary<string,MountEntry> _ids=new(StringComparer.Ordinal);
    private readonly Dictionary<string,MountEntry> _activePaths=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,Replacement> _replacements=new(StringComparer.Ordinal);
    private readonly Node _deleted=new();
    private sealed class Replacement
    {
        internal int Count;
        internal MountEntry First=null!;
        internal readonly Dictionary<string,MountEntry> Ids=new(StringComparer.Ordinal);
    }
    private sealed class Node
    {
        internal readonly Dictionary<char,Node> Children=[];
        internal MountEntry? Entry;
        internal int Order=int.MaxValue;
    }
    internal LocalUploadScanIndex(IEnumerable<MountEntry> entries)
    {
        var order=0;
        foreach(var entry in entries)
        {
            _ids.TryAdd(entry.Id,entry);
            if(!entry.Deleted)_activePaths.TryAdd(entry.LocalPath,entry);
            if(entry.Deleted&&entry.Materialized&&entry.RemotePresence!=RemotePresence.Deleted&&entry.LocalPath==entry.SourcePath)
            {
                if(!_replacements.TryGetValue(entry.LocalPath,out var replacement))_replacements.Add(entry.LocalPath,replacement=new(){First=entry});
                replacement.Count++;replacement.Ids.TryAdd(entry.Id,entry);
            }
            if(entry.RemotePresence==RemotePresence.Deleted)
            {
                var node=_deleted;
                foreach(var character in entry.SourcePath)
                {
                    if(!node.Children.TryGetValue(character,out var child))node.Children.Add(character,child=new());
                    node=child;
                }
                if(node.Entry is null){node.Entry=entry;node.Order=order;}
            }
            order++;
        }
    }
    internal MountEntry? ById(string id)=>_ids.GetValueOrDefault(id);
    internal MountEntry? ActivePath(string path)=>_activePaths.GetValueOrDefault(path);
    internal MountEntry? ReplacementBaseline(string path,string? id=null)
    {
        if(!_replacements.TryGetValue(path,out var replacement))return null;
        return id is not null?replacement.Ids.GetValueOrDefault(id):replacement.Count==1?replacement.First:null;
    }
    internal MountEntry? DeletedAncestor(string path)=>DeletedAncestor(path,out _);
    internal MountEntry? DeletedAncestor(string path,out int steps)
    {
        var node=_deleted;MountEntry? result=null;var order=int.MaxValue;steps=0;
        for(var index=0;;index++)
        {
            if(node.Entry is not null&&node.Order<order&&(index==path.Length||path[index]=='/')){result=node.Entry;order=node.Order;}
            if(index==path.Length)break;
            steps++;
            if(!node.Children.TryGetValue(path[index],out var child))break;
            node=child;
        }
        return result;
    }
}
