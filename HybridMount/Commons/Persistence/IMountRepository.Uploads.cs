using HybridMount.Models.Mounting;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    public IReadOnlyList<LocalUpload> Uploads(Guid? mount = null) ;
    internal IReadOnlyList<LocalUpload> UnfinishedUploads(Guid? mount = null) ;
    internal IReadOnlyList<LocalUpload> UploadsNeedingAttention(Guid? mount = null) ;
    internal IReadOnlyList<LocalUpload> UploadsAtPath(Guid mount, string path) ;
    internal int UploadAttentionCount(Guid mount);
    internal Dictionary<Guid, int> UploadAttentionCounts();
    public LocalUpload? Upload(string id);
    internal void RecoverUploadAttention();
    public void SaveUpload(LocalUpload upload);
    public LocalUpload? UpdateUpload(string id,Func<LocalUpload,LocalUpload> update);
    public void SaveBaseline(Guid mount,SourceContentBaseline baseline);
    public string? Baseline(Guid mount,string path,string version);
    internal LocalUpload? TryCommitUploadedEntry(LocalUpload captured,LocalUpload prepared,MountEntry entry,SourceEntry receipt,LocalFileStamp? committedStamp,bool missing=false,bool contentChanged=false);
    public LocalUpload CommitUploadedEntry(LocalUpload prepared,MountEntry entry,SourceEntry receipt,LocalFileStamp? committedStamp,bool missing=false,bool contentChanged=false);
}
