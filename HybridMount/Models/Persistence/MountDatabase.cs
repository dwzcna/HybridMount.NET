using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Persistence;

/// <summary>拥有当前版SQLite索引；创建前只读检查格式，旧索引不迁移，领域事务由仓库拥有。</summary>
internal sealed class MountDatabase
{
    public string DataDirectory {get;}
    public MountDatabase(string directory,IReadOnlyList<IMountProtocolStorage> protocolStorage)
    {
        DataDirectory=Path.GetFullPath(directory);MountIndexFormat.Inspect(DataDirectory).RequireCurrent();Directory.CreateDirectory(DataDirectory);
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText="PRAGMA user_version";
        if(Convert.ToInt32(cmd.ExecuteScalar())>MountIndexFormat.CurrentVersion)throw new IOException("挂载索引版本较新，请使用新版 HybridMount。");
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entries(mount TEXT NOT NULL,id TEXT NOT NULL,source TEXT NOT NULL COLLATE NOCASE,
                local TEXT NOT NULL COLLATE NOCASE,version TEXT NOT NULL,directory INTEGER NOT NULL,deleted INTEGER NOT NULL,
                protected INTEGER NOT NULL,materialized INTEGER NOT NULL,PRIMARY KEY(mount,id));
            CREATE INDEX IF NOT EXISTS entries_source ON entries(mount,source);
            CREATE INDEX IF NOT EXISTS entries_local ON entries(mount,local);
            CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS task_intents(id TEXT PRIMARY KEY,mount TEXT NOT NULL,session TEXT NOT NULL,sequence INTEGER NOT NULL,path TEXT NOT NULL COLLATE NOCASE,json TEXT NOT NULL,state TEXT NOT NULL,UNIQUE(mount,session,sequence));
            CREATE INDEX IF NOT EXISTS task_intents_ready ON task_intents(mount,state,sequence);
            CREATE INDEX IF NOT EXISTS task_intents_path ON task_intents(mount,path,state);
            CREATE TABLE IF NOT EXISTS task_sequences(mount TEXT NOT NULL,session TEXT NOT NULL,sequence INTEGER NOT NULL,PRIMARY KEY(mount,session));
            CREATE TABLE IF NOT EXISTS file_tasks(id TEXT PRIMARY KEY,mount TEXT NOT NULL,path TEXT NOT NULL COLLATE NOCASE,json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS file_tasks_path ON file_tasks(mount,path);
            CREATE TABLE IF NOT EXISTS task_steps(id TEXT PRIMARY KEY,task TEXT NOT NULL,ordinal INTEGER NOT NULL,json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS task_steps_task ON task_steps(task,ordinal);
            CREATE TABLE IF NOT EXISTS task_dependencies(task TEXT NOT NULL,required TEXT NOT NULL,PRIMARY KEY(task,required));
            CREATE TABLE IF NOT EXISTS task_attempts(id TEXT PRIMARY KEY,task TEXT NOT NULL,step TEXT NOT NULL,state TEXT NOT NULL,started TEXT NOT NULL,finished TEXT);
            CREATE TABLE IF NOT EXISTS intent_tasks(intent TEXT NOT NULL,task TEXT NOT NULL,PRIMARY KEY(intent,task));
            CREATE INDEX IF NOT EXISTS intent_tasks_task ON intent_tasks(task);
            CREATE TABLE IF NOT EXISTS task_outbox(id TEXT PRIMARY KEY,mount TEXT NOT NULL,json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS task_outbox_mount ON task_outbox(mount);
            CREATE INDEX IF NOT EXISTS task_outbox_intent ON task_outbox(json_extract(json,'$.TaskId'));
            CREATE TABLE IF NOT EXISTS uploads(id TEXT PRIMARY KEY,mount TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS uploads_mount ON uploads(mount);
            CREATE INDEX IF NOT EXISTS uploads_source ON uploads(mount,json_extract(json,'$.SourcePath') COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS content_baselines(mount TEXT NOT NULL,path TEXT NOT NULL,version TEXT NOT NULL,sha256 TEXT NOT NULL,
                PRIMARY KEY(mount,path,version));
            CREATE TABLE IF NOT EXISTS remote_presence(mount TEXT NOT NULL,id TEXT NOT NULL,presence INTEGER NOT NULL,PRIMARY KEY(mount,id));
            CREATE TABLE IF NOT EXISTS remote_collections(mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(mount,scope,path));
            CREATE TABLE IF NOT EXISTS remote_shadow(mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(mount,scope,path));
            CREATE TABLE IF NOT EXISTS remote_pending(id TEXT PRIMARY KEY,mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS remote_scans(id TEXT PRIMARY KEY,mount TEXT NOT NULL,scope TEXT NOT NULL,state TEXT NOT NULL,started TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS remote_pending_mount ON remote_pending(mount,scope);
            CREATE INDEX IF NOT EXISTS remote_pending_path ON remote_pending(mount,scope,path);
            CREATE INDEX IF NOT EXISTS remote_pending_mount_path ON remote_pending(mount,path COLLATE NOCASE);
            """+"\n"+string.Join("\n",protocolStorage.Select(module=>module.SchemaSql))+"\nPRAGMA user_version=7;"; cmd.ExecuteNonQuery();
    }
    public SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "mounts.db"), Pooling = false }.ToString()); db.Open(); return db; }
}
