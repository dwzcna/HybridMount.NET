using System.Runtime.CompilerServices;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;

namespace HybridMount.Services.Mounting;

/// <summary>持久模拟源：不可变内容文件加 SQLite 指针提交，条件写与元数据切换在同一串行事务中完成。</summary>
public sealed class MockFileSource : IFileSource, IDisposable
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public FileSourceCapabilities Capabilities { get; } = new(CapabilitySupport.Supported, CapabilitySupport.Supported,
        CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Supported, SourceCaseSensitivity.Insensitive);
    public MockFileSource(string directory)
    {
        _root = Path.GetFullPath(directory); Directory.CreateDirectory(Path.Combine(_root, "content"));
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS objects(id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                directory INTEGER NOT NULL,size INTEGER NOT NULL,modified TEXT NOT NULL,version TEXT NOT NULL,blob TEXT);
            INSERT OR IGNORE INTO objects VALUES('root','',1,0,'2026-01-01T00:00:00Z','root',NULL);
            CREATE TABLE IF NOT EXISTS state(name TEXT PRIMARY KEY,value TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "source.db"), Pooling = false }.ToString()); db.Open(); return db; }
    private static SourceEntry Map(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetInt64(2) != 0, row.GetInt64(3), DateTimeOffset.Parse(row.GetString(4)), row.GetString(5));
    private static SourceEntry? Find(SqliteConnection db, string path)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,path,directory,size,modified,version FROM objects WHERE path=$p"; cmd.Parameters.AddWithValue("$p", path);
        using var row = cmd.ExecuteReader(); return row.Read() ? Map(row) : null;
    }
    private static List<SourceEntry> All(SqliteConnection db)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,path,directory,size,modified,version FROM objects ORDER BY length(path),path";
        using var row = cmd.ExecuteReader(); var list = new List<SourceEntry>(); while (row.Read()) list.Add(Map(row)); return list;
    }
    private static void ParentExists(SqliteConnection db, string path)
    { if (path == "" || Find(db, RelativePaths.Parent(path)) is not { IsDirectory: true }) throw new DirectoryNotFoundException("源父目录不存在。"); }
    public async Task<SourceEntry> GetMetadataAsync(string path, CancellationToken token = default)
    {
        path = RelativePaths.Normalize(path); await _gate.WaitAsync(token).ConfigureAwait(false);
        try { using var db = Open(); return Find(db, path) ?? throw new FileNotFoundException("源条目不存在。", path); } finally { _gate.Release(); }
    }
    public async IAsyncEnumerable<SourceEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken token = default)
    {
        path = RelativePaths.Normalize(path); List<SourceEntry> entries;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { using var db = Open(); if (Find(db, path) is not { IsDirectory: true }) throw new DirectoryNotFoundException(path); entries = All(db).Where(e => e.Path != "" && RelativePaths.Parent(e.Path).Equals(path, StringComparison.OrdinalIgnoreCase)).ToList(); }
        finally { _gate.Release(); }
        foreach (var entry in entries) { token.ThrowIfCancellationRequested(); yield return entry; }
    }
    public async ValueTask<int> ReadRangeAsync(SourceEntry entry, long offset, Memory<byte> buffer, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset); string blob;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var db = Open(); var current = Find(db, entry.Path) ?? throw new FileNotFoundException(entry.Path);
            if (current.Version != entry.Version || current.StableId != entry.StableId) throw new SourceConflictException("源对象版本已变化。");
            if (current.IsDirectory) throw new IOException("目录不能读取内容。");
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT blob FROM objects WHERE id=$id"; cmd.Parameters.AddWithValue("$id", entry.StableId); blob = (string)cmd.ExecuteScalar()!;
        }
        finally { _gate.Release(); }
        await using var stream = new FileStream(Path.Combine(_root, "content", blob), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = offset; return await stream.ReadAsync(buffer, token).ConfigureAwait(false);
    }
    public async Task<SourceEntry> UploadAsync(string path, Stream content, string? expectedVersion, CancellationToken token = default)
    {
        path = RelativePaths.Normalize(path); var blob = Guid.NewGuid().ToString("N"); var file = Path.Combine(_root, "content", blob);
        var committed = false;
        try
        {
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await content.CopyToAsync(output, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(); ParentExists(db, path);
                var existing = Find(db, path);
                if (existing?.IsDirectory == true || (existing is null ? expectedVersion is not null : expectedVersion != existing.Version)) throw new SourceConflictException("创建条件或对象版本不匹配。");
                var entry = new SourceEntry(existing?.StableId ?? Guid.NewGuid().ToString("N"), path, false, new FileInfo(file).Length, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
                using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO objects VALUES($id,$p,0,$s,$m,$v,$b) ON CONFLICT(id) DO UPDATE SET size=$s,modified=$m,version=$v,blob=$b";
                cmd.Parameters.AddWithValue("$id", entry.StableId); cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$s", entry.Size); cmd.Parameters.AddWithValue("$m", entry.ModifiedUtc.ToString("O")); cmd.Parameters.AddWithValue("$v", entry.Version); cmd.Parameters.AddWithValue("$b", blob);
                cmd.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit(); committed = true; return entry;
            }
            finally { _gate.Release(); }
        }
        finally { if (!committed) File.Delete(file); }
    }
    public async Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token = default)
    {
        path = RelativePaths.Normalize(path); await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var db = Open(); ParentExists(db, path); if (Find(db, path) is not null) throw new SourceConflictException("源名称已存在。");
            var entry = new SourceEntry(Guid.NewGuid().ToString("N"), path, true, 0, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
            using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO objects VALUES($id,$p,1,0,$m,$v,NULL)";
            cmd.Parameters.AddWithValue("$id", entry.StableId); cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$m", entry.ModifiedUtc.ToString("O")); cmd.Parameters.AddWithValue("$v", entry.Version); cmd.ExecuteNonQuery(); return entry;
        }
        finally { _gate.Release(); }
    }
    public async Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token = default)
    {
        path = RelativePaths.Normalize(path); destination = RelativePaths.Normalize(destination);
        if (path == "" || destination == "" || RelativePaths.Contains(path, destination)) throw new IOException("不能移动根或将目录移入自身。");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(); ParentExists(db, destination);
            var entry = Find(db, path) ?? throw new FileNotFoundException(path);
            if (entry.Version != expectedVersion || Find(db, destination) is not null) throw new SourceConflictException("移动条件不满足。");
            var version = Guid.NewGuid().ToString("N");
            foreach (var item in All(db).Where(item => RelativePaths.Contains(path, item.Path)))
            {
                using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE objects SET path=$p,version=$v WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", item.StableId); cmd.Parameters.AddWithValue("$p", destination + item.Path[path.Length..]); cmd.Parameters.AddWithValue("$v", item.StableId == entry.StableId ? version : item.Version); cmd.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested(); tx.Commit(); return entry with { Path = destination, Version = version };
        }
        finally { _gate.Release(); }
    }
    public async Task DeleteAsync(string path, string expectedVersion, CancellationToken token = default)
    {
        path = RelativePaths.Normalize(path); if (path == "") throw new IOException("不能删除源根。"); await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(); var entry = Find(db, path) ?? throw new FileNotFoundException(path);
            if (entry.Version != expectedVersion) throw new SourceConflictException("删除版本冲突。");
            foreach (var item in All(db).Where(item => RelativePaths.Contains(path, item.Path)))
            { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "DELETE FROM objects WHERE id=$id"; cmd.Parameters.AddWithValue("$id", item.StableId); cmd.ExecuteNonQuery(); }
            token.ThrowIfCancellationRequested(); tx.Commit();
        }
        finally { _gate.Release(); }
    }
    /// <summary>仅首次初始化确定的数据树；已有对象和用户通过契约写入的数据不覆盖。</summary>
    public async Task SeedAsync(CancellationToken token = default)
    {
        using(var db=Open()){using var check=db.CreateCommand();check.CommandText="SELECT value FROM state WHERE name='seed'";if(check.ExecuteScalar() is string)return;}
        async Task<bool> Exists(string path){try{await GetMetadataAsync(path,token).ConfigureAwait(false);return true;}catch(FileNotFoundException){return false;}}
        foreach(var directory in new[]{"中文目录","中文目录/多层"})if(!await Exists(directory).ConfigureAwait(false))await CreateDirectoryAsync(directory,token).ConfigureAwait(false);
        foreach(var item in new[]{("说明.txt","HybridMount.NET 模拟文件源：内容按需下载。\r\n"),("空文件.txt",""),("中文目录/多层/示例.txt","本地修改不会自动上传，修改项不会被释放。")})
            if(!await Exists(item.Item1).ConfigureAwait(false))await UploadAsync(item.Item1,new MemoryStream(System.Text.Encoding.UTF8.GetBytes(item.Item2)),null,token).ConfigureAwait(false);
        if(!await Exists("范围读取.bin").ConfigureAwait(false))
        {
            var large=new byte[32*1024*1024+37];for(var index=0;index<large.Length;index++)large[index]=(byte)(index*31+17);
            await UploadAsync("范围读取.bin",new MemoryStream(large,false),null,token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();using(var db=Open()){using var done=db.CreateCommand();done.CommandText="INSERT OR REPLACE INTO state VALUES('seed','1')";done.ExecuteNonQuery();}
    }
    public void Dispose() => _gate.Dispose();
}
