using System.Runtime.CompilerServices;
using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class S3FileSource : IRemoteHintSource
{
    /// <summary>事件不直接删除；按 Key 重新 HEAD，并确认现存父集合后才向公共同步器返回变化。</summary>
    public async IAsyncEnumerable<RemoteHintChange> ReadHintChangesAsync(IRemoteHintJournal journal,Guid mount,string scope,[EnumeratorCancellation]CancellationToken token)
    {
        foreach(var hint in journal.PendingRemoteHints(mount,scope))
        {
            if(_sessionProbeKeys.ContainsKey(Key(hint.Path))){token.ThrowIfCancellationRequested();yield return new(hint.Id,[],[]);continue;}
            token.ThrowIfCancellationRequested();var metadata=await TryGetMetadataAsync(hint.Path,token).ConfigureAwait(false);var entries=new List<SourceEntry>();
            if(metadata is not null)
            {
                for(var parent=RelativePaths.Parent(metadata.Path);parent.Length!=0;parent=RelativePaths.Parent(parent))
                {
                    var collection=await TryGetMetadataAsync(parent,token).ConfigureAwait(false);
                    if(collection?.IsDirectory!=true)throw new SourceConflictException("S3 事件路径的当前父集合不完整，保留提示待重试。");entries.Insert(0,collection);
                }
                entries.Add(metadata);
            }
            yield return new(hint.Id,entries,metadata is null?[hint.Path]:[]);
        }
    }
}
/// <summary>原内部调用入口兼容转发；公共协调器不依赖 S3 提示类型。</summary>
internal sealed partial class RemoteSyncEngine
{
    public Task ObserveS3HintsAsync(CancellationToken token)=>ObserveEventHintsAsync(token);
}
