using HybridMount.Models.Mounting;
namespace HybridMount.Common;

/// <summary>协议无关的文件源契约；路径相对源根，版本是不透明条件令牌，取消不能推断写入回滚。</summary>
public interface IFileSource : IAsyncDisposable
{
    FileSourceCapabilities Capabilities { get; }
    /// <summary>按具体写操作查询已验证能力；旧适配器保持未知。</summary>
    SourceWriteCapabilities WriteCapabilities => new();
    /// <summary>不透明版本默认严格相等；协议只能归一化可证明等价的表示。</summary>
    bool IsSameVersion(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);
    /// <summary>只读连接探测；不得在此自动创建或删除用户的远端内容。</summary>
    Task ProbeAsync(CancellationToken token = default) => Task.CompletedTask;
    /// <summary>只读服务器容量；无标准接口的适配器返回未知，不能以本地磁盘或对象合计代替。</summary>
    Task<RemoteCapacity> GetRemoteCapacityAsync(CancellationToken token=default)=>Task.FromResult(RemoteCapacity.Unknown);
    /// <summary>会话停止后释放连接；兼容测试源的同步释放。</summary>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (this is IDisposable disposable) disposable.Dispose();
        return ValueTask.CompletedTask;
    }
    IAsyncEnumerable<SourceEntry> ListAsync(string path, CancellationToken token = default);
    Task<SourceEntry> GetMetadataAsync(string path, CancellationToken token = default);
    /// <summary>仅明确不存在时返回空；认证、权限、网络及取消继续抛出。旧适配器保持兼容。</summary>
    async Task<SourceEntry?> TryGetMetadataAsync(string path,CancellationToken token=default)
    {try{return await GetMetadataAsync(path,token).ConfigureAwait(false);}catch(FileNotFoundException){return null;}}
    ValueTask<int> ReadRangeAsync(SourceEntry entry, long offset, Memory<byte> buffer, CancellationToken token = default);
    /// <summary>顺序传递完整分块（末块可短）；缓冲仅在 sink 返回前有效，消费者异常不得重试。</summary>
    Task ReadRangeSequentialAsync(SourceEntry entry,long offset,long length,Func<ReadOnlyMemory<byte>,CancellationToken,ValueTask> sink,int chunk=262144,CancellationToken token=default)
        => SequentialRangeTransfer.DefaultAsync(this,entry,offset,length,sink,chunk,token);
    /// <summary>严格预览范围读取；默认不支持，不允许整文件退化或触发 CFAPI 水合。</summary>
    ValueTask<PreviewRangeResult> TryReadPreviewRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default) => ValueTask.FromResult(new PreviewRangeResult(false,0));
    /// <summary>expectedVersion 为空表示仅创建；更新必须匹配当前版本。</summary>
    Task<SourceEntry> UploadAsync(string path, Stream content, string? expectedVersion, CancellationToken token = default);
    /// <summary>上传不可变快照；失败或取消不代表远端回滚。旧驱动继续使用版本条件，不能忽略额外摘要。</summary>
    Task<SourceEntry> UploadWithPreconditionAsync(string path, Stream content, UploadPrecondition condition,
        IProgress<UploadProgress>? progress = null, CancellationToken token = default)
        => UploadAsync(path, content, condition.ExpectedVersion, token);
    Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token = default);
    Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token = default);
    Task DeleteAsync(string path, string expectedVersion, CancellationToken token = default);
}

internal sealed class SequentialRangeConsumerException(Exception inner):Exception("顺序读取消费者失败。",inner);

internal sealed class SequentialRangeTransfer
{
    private readonly byte[] _buffer;
    private readonly Func<ReadOnlyMemory<byte>,CancellationToken,ValueTask> _sink;
    private int _filled;
    public long Length {get;}
    public long Consumed {get;private set;}
    public SequentialRangeTransfer(SourceEntry entry,long offset,long length,Func<ReadOnlyMemory<byte>,CancellationToken,ValueTask> sink,int chunk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);ArgumentOutOfRangeException.ThrowIfNegative(length);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunk);ArgumentNullException.ThrowIfNull(sink);
        if(entry.IsDirectory||offset>entry.Size||length>entry.Size-offset)throw new ArgumentException("顺序读取范围无效。");
        Length=length;_sink=sink;_buffer=new byte[(int)Math.Min(chunk,length)];
    }
    private async ValueTask DeliverAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try{await _sink(_buffer.AsMemory(0,_filled),token).ConfigureAwait(false);}
        catch(Exception error){throw new SequentialRangeConsumerException(error);}
        _filled=0;
    }
    public async Task ReadAsync(Stream stream,CancellationToken token)
    {
        while(Consumed<Length)
        {
            token.ThrowIfCancellationRequested();var target=(int)Math.Min(_buffer.Length,Length-(Consumed-_filled));
            var read=await stream.ReadAsync(_buffer.AsMemory(_filled,target-_filled),token).ConfigureAwait(false);if(read==0)throw new EndOfStreamException("远端区间提前结束。");
            Consumed+=read;_filled+=read;
            if(_filled==target&&Consumed<Length)await DeliverAsync(token).ConfigureAwait(false);
        }
        try{if(await stream.ReadAsync(new byte[1],token).ConfigureAwait(false)!=0)throw new IOException("远端区间包含多余字节。");}
        catch(Exception error){throw new SequentialRangeConsumerException(error);}
        if(_filled>0)await DeliverAsync(token).ConfigureAwait(false);
    }
    public async Task ReadRangesAsync(Func<long,Memory<byte>,CancellationToken,ValueTask<int>> readRange,CancellationToken token)
    {
        while(Consumed<Length)
        {
            token.ThrowIfCancellationRequested();var target=(int)Math.Min(_buffer.Length,Length-(Consumed-_filled));
            var read=await readRange(Consumed,_buffer.AsMemory(_filled,target-_filled),token).ConfigureAwait(false);
            if(read<=0||read>target-_filled)throw new EndOfStreamException("远端区间提前结束。");Consumed+=read;_filled+=read;
            if(_filled==target)await DeliverAsync(token).ConfigureAwait(false);
        }
    }
    public static async Task DefaultAsync(IFileSource source,SourceEntry entry,long offset,long length,Func<ReadOnlyMemory<byte>,CancellationToken,ValueTask> sink,int chunk,CancellationToken token)
    {
        var transfer=new SequentialRangeTransfer(entry,offset,length,sink,chunk);token.ThrowIfCancellationRequested();var bytes=transfer._buffer;long total=0;
        while(total<length)
        {
            var count=(int)Math.Min(bytes.Length,length-total);var filled=0;
            while(filled<count)
            {
                token.ThrowIfCancellationRequested();var read=await source.ReadRangeAsync(entry,offset+total+filled,bytes.AsMemory(filled,count-filled),token).ConfigureAwait(false);
                if(read<=0||read>count-filled)throw new EndOfStreamException("远端区间提前结束。");filled+=read;
            }
            token.ThrowIfCancellationRequested();await sink(bytes.AsMemory(0,count),token).ConfigureAwait(false);total+=count;
        }
    }
}
