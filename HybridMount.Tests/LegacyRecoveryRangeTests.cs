using System.Security.Cryptography;
using HybridMount.Models.Mounting.Sources;
using HybridMount.Services.Mounting;
using Microsoft.Data.Sqlite;

namespace HybridMount.Tests;

/// <summary>旧源读取只使用独立SQLite和内容文件，验证合并查询后仍拒绝版本、身份及内容指针变化。</summary>
public sealed class LegacyRecoveryRangeTests
{
    [Fact]
    public async Task ReadsRangesWithoutWritingSourceAndNormalizesRequestedPath()
    {
        var area = Directory.CreateTempSubdirectory("HybridMount.LegacyRange.").FullName;
        try
        {
            using var seed = new MockFileSource(area);
            var bytes = Enumerable.Range(0, 257).Select(index => (byte)index).ToArray();
            var entry = await seed.UploadAsync("file.bin", new MemoryStream(bytes), null);
            var source = new LegacyRecoverySource(area);
            var database = Path.Combine(area, "source.db");
            var before = SHA256.HashData(await File.ReadAllBytesAsync(database));
            var buffer = new byte[41];
            Assert.Equal(buffer.Length, await source.ReadRangeAsync(entry with { Path = "FILE.bin" }, 23, buffer));
            Assert.Equal(bytes.AsSpan(23, buffer.Length).ToArray(), buffer);
            Assert.Equal(0, await source.ReadRangeAsync(entry, bytes.Length, buffer));
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(database)));
        }
        finally { Directory.Delete(area, true); }
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("blob")]
    [InlineData("missing")]
    public async Task InvalidIdentityVersionAndBlobAreRejected(string failure)
    {
        var area = Directory.CreateTempSubdirectory("HybridMount.LegacyInvalid.").FullName;
        try
        {
            using var seed = new MockFileSource(area);
            var entry = await seed.UploadAsync("file.bin", new MemoryStream([1, 2, 3]), null);
            var source = new LegacyRecoverySource(area);
            if (failure == "identity") entry = entry with { StableId = "other" };
            if (failure == "version") entry = entry with { Version = "other" };
            if (failure == "missing") entry = entry with { Path = "missing.bin" };
            if (failure == "blob")
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(area, "source.db"), Pooling = false }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE objects SET blob='../outside' WHERE path='file.bin'";
                command.ExecuteNonQuery();
            }
            var error = await Assert.ThrowsAnyAsync<IOException>(async () => await source.ReadRangeAsync(entry, 0, new byte[3]));
            if (failure is "identity" or "version") Assert.IsType<SourceConflictException>(error);
            if (failure == "missing") Assert.IsType<FileNotFoundException>(error);
        }
        finally { Directory.Delete(area, true); }
    }

    [Fact]
    public async Task CancellationAndNegativeOffsetAreRejectedBeforeDatabaseOpen()
    {
        var source = new LegacyRecoverySource(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var entry = new SourceEntry("entry", "file.bin", false, 3, DateTimeOffset.UtcNow, "v");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.ReadRangeAsync(entry, 0, new byte[3], stop.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await source.ReadRangeAsync(entry, -1, new byte[3]));
    }
}
