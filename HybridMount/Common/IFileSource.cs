using HybridMount.Models.Mounting;
namespace HybridMount.Common;

/// <summary>协议无关的文件源契约；路径相对源根，版本是不透明条件令牌，取消不能推断写入回滚。</summary>
public interface IFileSource : IAsyncDisposable
{
    FileSourceCapabilities Capabilities { get; }
    /// <summary>只读连接探测；不得在此自动创建或删除用户的远端内容。</summary>
    Task ProbeAsync(CancellationToken token = default) => Task.CompletedTask;
    /// <summary>会话停止后释放连接；兼容测试源的同步释放。</summary>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (this is IDisposable disposable) disposable.Dispose();
        return ValueTask.CompletedTask;
    }
    IAsyncEnumerable<SourceEntry> ListAsync(string path, CancellationToken token = default);
    Task<SourceEntry> GetMetadataAsync(string path, CancellationToken token = default);
    ValueTask<int> ReadRangeAsync(SourceEntry entry, long offset, Memory<byte> buffer, CancellationToken token = default);
    /// <summary>expectedVersion 为空表示仅创建；更新必须匹配当前版本。</summary>
    Task<SourceEntry> UploadAsync(string path, Stream content, string? expectedVersion, CancellationToken token = default);
    Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token = default);
    Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token = default);
    Task DeleteAsync(string path, string expectedVersion, CancellationToken token = default);
}
