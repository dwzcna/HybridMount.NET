using System.Security.Cryptography;
using System.Text;
using Amazon.S3.Model;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.S3;

public sealed partial class S3FileSource
{
    private readonly string _remoteSyncIdentity;
    /// <summary>游标、消息与观测按远端根和凭据修订隔离；只存摘要，不写凭据或账号标识到日志。</summary>
    public string RemoteScope(long revision)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_remoteSyncIdentity+"\0"+revision)));
    public Task<bool> SupportsCollectionSyncAsync(string collection,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(false);}
    public Task<RemoteChangeBatch> GetCollectionChangesAsync(string collection,string? syncToken,CancellationToken token)=>throw new NotSupportedException("S3 使用完整对账，不能把列表分页令牌作为增量检查点。");
    public SourceEntry WithObservation(SourceEntry entry,long observation)=>entry;
    public void RestoreObservations(IEnumerable<RemoteShadowEntry> entries){}
    public bool HasReliableVersion(SourceEntry entry)=>entry.IsDirectory||Version(entry).ETag is {Length:>0};
    /// <summary>完整前缀无分隔符枚举；只在全部页成功后 HEAD 变化对象，未成功不向调用方输出部分结果。</summary>
    public async Task<IReadOnlyList<SourceEntry>> ReadSnapshotAsync(IReadOnlyList<RemoteShadowEntry> previous,CancellationToken token)
    {
        var objects=new Dictionary<string,S3Object>(StringComparer.Ordinal);var tokens=new HashSet<string>(StringComparer.Ordinal);string? continuation=null;
        do
        {
            var page=await Call(()=>_client.ListObjectsV2Async(new(){BucketName=_bucket,Prefix=_prefix,ContinuationToken=continuation,MaxKeys=1000},token),token).ConfigureAwait(false);
            foreach(var item in page.S3Objects??[])
            {
                if(item.Key is not null&&_sessionProbeKeys.ContainsKey(item.Key))continue;
                if(item.Key is null||!item.Key.StartsWith(_prefix,StringComparison.Ordinal)||item.Size is null or <0||item.LastModified is null||string.IsNullOrEmpty(item.ETag)||!objects.TryAdd(item.Key,item))
                    throw new IOException("S3 快照含越界、重复或不完整对象，未提交扫描。");
                if(objects.Count>1000000)throw new IOException("S3 完整快照超过安全数量限制。");
            }
            if(page.CommonPrefixes?.Count>0||page.IsTruncated is null)throw new IOException("S3 无分隔符快照结果不完整，未提交扫描。");
            continuation=page.IsTruncated.Value?page.NextContinuationToken:null;
            if(page.IsTruncated.Value&&(string.IsNullOrEmpty(continuation)||!tokens.Add(continuation)))throw new IOException("S3 快照分页缺失或重复，未提交扫描。");
        }while(continuation is not null);
        token.ThrowIfCancellationRequested();
        var known=previous.ToDictionary(x=>x.Entry.Path,StringComparer.Ordinal);var entries=new Dictionary<string,SourceEntry>(StringComparer.Ordinal);
        var types=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);var names=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Reserve(string path,bool directory)
        {
            if(path.Length==0)return;RelativePaths.Normalize(path);
            if(names.TryGetValue(path,out var original)&&original!=path||types.TryGetValue(path,out var prior)&&prior!=directory)throw new SourceConflictException("S3 快照文件/目录或大小写冲突，不能自动映射。");
            names[path]=path;types[path]=directory;if(types.Count>1000000)throw new IOException("S3 目录映射超过安全数量限制。");
        }
        foreach(var (key,item) in objects)
        {
            token.ThrowIfCancellationRequested();var relative=key[_prefix.Length..];var directory=key.EndsWith('/');var path=directory&&relative.Length>0?relative[..^1]:relative;
            if(path.Length==0){if(!directory||item.Size!=0)throw new IOException("S3 根目录标记无效。");continue;}
            Reserve(path,directory);if(directory&&item.Size!=0)throw new IOException("S3 目录标记不是零字节对象。");
            for(var parent=RelativePaths.Parent(path);parent.Length!=0;parent=RelativePaths.Parent(parent))Reserve(parent,true);
            known.TryGetValue(path,out var old);var version=old is null?null:Version(old.Entry);
            var modified=new DateTimeOffset(DateTime.SpecifyKind(item.LastModified!.Value,DateTimeKind.Utc));
            if(old is not null&&old.Entry.IsDirectory==directory&&version?.ETag==item.ETag&&old.Entry.Size==(directory?0:item.Size)&&old.Entry.ModifiedUtc==modified)
                entries[path]=old.Entry;
            else
            {
                GetObjectMetadataResponse head;
                try{head=await HeadAsync(key,token).ConfigureAwait(false);}
                catch(FileNotFoundException){throw new SourceRescanRequiredException("S3 列表后对象已变化，正在重新完整扫描。");}
                var current=Metadata(path,head,directory);
                if(head.ETag!=item.ETag||head.ContentLength!=item.Size||current.ModifiedUtc.ToUnixTimeSeconds()!=modified.ToUnixTimeSeconds())throw new SourceRescanRequiredException("S3 分页或 HEAD 期间对象变化，正在重新完整扫描。");
                // ListObjects 的时间可含毫秒，HTTP Last-Modified 只有整秒；校验共同精度，索引仍保存列表精确时间。
                entries[path]=current with{ModifiedUtc=modified};
            }
        }
        foreach(var (path,directory) in types)if(directory&&!entries.ContainsKey(path))entries[path]=VirtualDirectory(path);
        return entries.Values.OrderBy(x=>x.Path,StringComparer.Ordinal).ToArray();
    }
}
