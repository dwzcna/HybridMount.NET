using Microsoft.Data.Sqlite;
namespace HybridMount.Services.Mounting.Persistence;

/// <summary>仅拥有 SQLite 连接及兼容建表；保持 v6 原表、字段和事务调用方式，不参与领域状态转换。</summary>
internal sealed class MountDatabase
{
    public string DataDirectory {get;}
    public MountDatabase(string directory,IReadOnlyList<IMountProtocolStorage> protocolStorage)
    {
        DataDirectory=Path.GetFullPath(directory);Directory.CreateDirectory(DataDirectory);
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText="PRAGMA user_version";
        if(Convert.ToInt32(cmd.ExecuteScalar())>6)throw new IOException("挂载索引版本较新，请使用新版 HybridMount。");
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entries(mount TEXT NOT NULL,id TEXT NOT NULL,source TEXT NOT NULL COLLATE NOCASE,
                local TEXT NOT NULL COLLATE NOCASE,version TEXT NOT NULL,directory INTEGER NOT NULL,deleted INTEGER NOT NULL,
                protected INTEGER NOT NULL,materialized INTEGER NOT NULL,PRIMARY KEY(mount,id));
            CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS uploads(id TEXT PRIMARY KEY,mount TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS uploads_mount ON uploads(mount);
            CREATE TABLE IF NOT EXISTS content_baselines(mount TEXT NOT NULL,path TEXT NOT NULL,version TEXT NOT NULL,sha256 TEXT NOT NULL,
                PRIMARY KEY(mount,path,version));
            CREATE TABLE IF NOT EXISTS remote_presence(mount TEXT NOT NULL,id TEXT NOT NULL,presence INTEGER NOT NULL,PRIMARY KEY(mount,id));
            CREATE TABLE IF NOT EXISTS remote_collections(mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(mount,scope,path));
            CREATE TABLE IF NOT EXISTS remote_shadow(mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(mount,scope,path));
            CREATE TABLE IF NOT EXISTS remote_pending(id TEXT PRIMARY KEY,mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS remote_scans(id TEXT PRIMARY KEY,mount TEXT NOT NULL,scope TEXT NOT NULL,state TEXT NOT NULL,started TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS remote_pending_mount ON remote_pending(mount,scope);
            """+"\n"+string.Join("\n",protocolStorage.Select(module=>module.SchemaSql))+"\nPRAGMA user_version=6;"; cmd.ExecuteNonQuery();
    }
    public SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "mounts.db"), Pooling = false }.ToString()); db.Open(); return db; }
}
