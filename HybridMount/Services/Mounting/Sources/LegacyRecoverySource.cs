using System.Runtime.CompilerServices;
using HybridMount.Common;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;

namespace HybridMount.Services.Mounting;

/// <summary>0.0.2 模拟内容的只读迁出器；不播种、不写库、不创建新模拟挂载。</summary>
internal sealed class LegacyRecoverySource(string directory) : IFileSource
{
    public FileSourceCapabilities Capabilities { get; } = new(CapabilitySupport.Supported, CapabilitySupport.Unsupported,
        CapabilitySupport.Unsupported, CapabilitySupport.Unsupported, CapabilitySupport.Supported, SourceCaseSensitivity.Insensitive);
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "source.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
    public Task<SourceEntry> GetMetadataAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); path = RelativePaths.Normalize(path);
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id,path,directory,size,modified,version FROM objects WHERE path=$p"; command.Parameters.AddWithValue("$p", path);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new FileNotFoundException("旧版源内容不可用；请恢复备份后再迁出。");
        return Task.FromResult(new SourceEntry(row.GetString(0), row.GetString(1), row.GetInt64(2)!=0, row.GetInt64(3), DateTimeOffset.Parse(row.GetString(4)), row.GetString(5)));
    }
    public async ValueTask<int> ReadRangeAsync(SourceEntry entry, long offset, Memory<byte> buffer, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var current = await GetMetadataAsync(entry.Path, token).ConfigureAwait(false);
        if (entry.Version != current.Version || entry.StableId != current.StableId) throw new SourceConflictException("旧版源版本不匹配。");
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT blob FROM objects WHERE id=$id"; command.Parameters.AddWithValue("$id", entry.StableId);
        var blob = command.ExecuteScalar() as string;
        if (blob is null || !Guid.TryParseExact(blob,"N",out _)) throw new IOException("旧版内容身份无效。");
        await using var stream = new FileStream(Path.Combine(directory, "content", blob), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        stream.Position = offset; return await stream.ReadAsync(buffer, token).ConfigureAwait(false);
    }
    public async IAsyncEnumerable<SourceEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken token = default)
    { await Task.CompletedTask; token.ThrowIfCancellationRequested(); throw new NotSupportedException("旧源仅用于已有占位内容迁出。");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
    public Task<SourceEntry> UploadAsync(string path, Stream content, string? expectedVersion, CancellationToken token=default) => throw new NotSupportedException("旧版源只读。");
    public Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token=default) => throw new NotSupportedException("旧版源只读。");
    public Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token=default) => throw new NotSupportedException("旧版源只读。");
    public Task DeleteAsync(string path, string expectedVersion, CancellationToken token=default) => throw new NotSupportedException("旧版源只读。");
}
