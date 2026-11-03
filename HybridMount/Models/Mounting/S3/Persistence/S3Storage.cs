using Microsoft.Data.Sqlite;
namespace HybridMount.Services.Mounting;

/// <summary>S3 事件／去重日志的 schema 与谓词；调用方提供原连接与事务，字段和确认边界保持。</summary>
internal sealed class S3Storage : IMountProtocolStorage
{
    public string SchemaSql=>"CREATE TABLE IF NOT EXISTS s3_event_hints(id TEXT PRIMARY KEY,mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL);\nCREATE INDEX IF NOT EXISTS s3_event_hints_mount ON s3_event_hints(mount,scope);\nCREATE TABLE IF NOT EXISTS s3_event_watermarks(mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,sequence TEXT NOT NULL,PRIMARY KEY(mount,scope,path));\nCREATE TABLE IF NOT EXISTS sqs_messages(queue TEXT NOT NULL,id TEXT NOT NULL,received TEXT NOT NULL,PRIMARY KEY(queue,id));";
    public string DeleteEntriesSql=>"DELETE FROM s3_event_hints WHERE mount=$m;DELETE FROM s3_event_watermarks WHERE mount=$m";
    public string ActivateScopeSql=>"UPDATE s3_event_hints SET json=json_set(json,'$.State','Superseded') WHERE mount=$m AND scope<>$s AND json_extract(json,'$.State')='Pending';";
    public string? PendingPredicate=>"EXISTS (SELECT 1 FROM s3_event_hints WHERE mount=$m AND json_extract(json,'$.State')='Pending' AND (path=$p OR substr($p,1,length(path)+1)=path||'/'))";
    public void CompleteHint(MountRepository repository,SqliteConnection db,SqliteTransaction transaction,Guid mount,string scope,string id)
        =>repository.MarkS3HintCore(db,transaction,mount,scope,id);
}
