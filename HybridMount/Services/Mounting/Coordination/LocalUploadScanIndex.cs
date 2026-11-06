using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>单次扫描的只读索引；一次枚举保存原有首项顺序、大小写规则及删除屏障，构造后可并发查询。</summary>
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
    /// <summary>从本次索引快照构造查找，不修改仓库或文件系统；重复项仍沿用原线性规则。</summary>
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
    /// <summary>按精确条目ID返回首次映射，缺失返回空。</summary>
    internal MountEntry? ById(string id)=>_ids.GetValueOrDefault(id);
    /// <summary>按Windows路径大小写规则返回首个有效映射，不接管删除项。</summary>
    internal MountEntry? ActivePath(string path)=>_activePaths.GetValueOrDefault(path);
    /// <summary>同名替换只沿用精确路径和唯一或已绑定的历史基线，歧义时返回空。</summary>
    internal MountEntry? ReplacementBaseline(string path,string? id=null)
    {
        if(!_replacements.TryGetValue(path,out var replacement))return null;
        return id is not null?replacement.Ids.GetValueOrDefault(id):replacement.Count==1?replacement.First:null;
    }
    /// <summary>按路径边界寻找原输入顺序中最早的远端删除祖先，缺失返回空。</summary>
    internal MountEntry? DeletedAncestor(string path)=>DeletedAncestor(path,out _);
    /// <summary>查询工作量受路径长度约束；steps供隔离测试验证，不计入持久化状态。</summary>
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
