using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>仅记录持保护句柄核验通过的本程序元数据变化；根锁内使用，有界链证明不扩大正文授权。</summary>
internal sealed class ConflictMetadataChanges
{
    private readonly List<(string Path,string Before,string After)> _changes=[];
    internal void Record(string path,LocalFileStamp before,LocalFileStamp after)
    {
        if(before.Identity!=after.Identity||before.Length!=after.Length||before.LastWriteTime!=after.LastWriteTime||before.IsDirectory!=after.IsDirectory||before.Key==after.Key)return;
        _changes.Add((path,before.Key,after.Key));if(_changes.Count>512)_changes.RemoveAt(0);
    }
    /// <summary>必须从原完整状态戳沿本程序已确认的边到达当前完整戳；外部修改、替换或缺边均拒绝。</summary>
    internal bool Allows(string path,string? before,string? after)
    {
        if(before==after)return true;if(before is null||after is null)return false;
        var current=before;
        foreach(var change in _changes){if(change.Path==path&&change.Before==current){current=change.After;if(current==after)return true;}}
        return false;
    }
}
