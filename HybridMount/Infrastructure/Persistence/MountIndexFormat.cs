using Microsoft.Data.Sqlite;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>业务服务创建前的只读索引格式检查；只接纳schema9；旧格式、较新、损坏及访问失败分别报告，不升级或删除。</summary>
internal sealed record MountIndexFormat(string State,int Version=0,int? HResult=null)
{
    internal const int CurrentVersion=9;
    internal bool CanStart=>State is "New" or "Current";
    internal static MountIndexFormat Inspect(string directory)
    {
        var path=Path.Combine(directory,"mounts.db");
        try
        {
            // File.Exists会吞访问拒绝；显式读取属性以区分不存在和无法访问。
            try{_ = File.GetAttributes(path);}catch(FileNotFoundException){return new("New");}catch(DirectoryNotFoundException){return new("New");}
            // 提前核验实际读权限，避免SQLite的CANTOPEN把访问拒绝混为普通读取错误。
            using(var proof=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){}
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
            db.Open();MountDatabase.ConfigurePathComparison(db);using var command=db.CreateCommand();command.CommandText="PRAGMA quick_check(1)";
            if(command.ExecuteScalar() as string!="ok")return new("Corrupt");
            command.CommandText="PRAGMA user_version";var version=Convert.ToInt32(command.ExecuteScalar());
            if(version<CurrentVersion)return new("Old",version);
            if(version>CurrentVersion)return new("Newer",version);
            command.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('entries','operations','task_intents','task_sequences','file_tasks','task_steps','task_dependencies','task_attempts','intent_tasks','task_outbox','uploads','content_baselines','remote_presence','remote_collections','remote_shadow','remote_pending','remote_scans','operation_runs','operation_steps','remote_task_batches','remote_task_batch_members','remote_temporary','s3_event_hints','s3_event_watermarks','sqs_messages')";
            if(Convert.ToInt32(command.ExecuteScalar())!=25)return new("Corrupt",version);
            command.CommandText="""
                SELECT mount,id,source,local,version,directory,deleted,protected,materialized FROM entries LIMIT 0;
                SELECT id,json FROM operations LIMIT 0;
                SELECT id,mount,session,sequence,path,json,state FROM task_intents LIMIT 0;
                SELECT mount,session,sequence FROM task_sequences LIMIT 0;
                SELECT id,mount,path,json FROM file_tasks LIMIT 0;
                SELECT id,task,ordinal,json FROM task_steps LIMIT 0;
                SELECT task,required FROM task_dependencies LIMIT 0;
                SELECT id,task,step,state,started,finished FROM task_attempts LIMIT 0;
                SELECT intent,task FROM intent_tasks LIMIT 0;
                SELECT id,mount,json FROM task_outbox LIMIT 0;
                SELECT id,mount,path,json FROM uploads LIMIT 0;
                SELECT mount,path,version,sha256 FROM content_baselines LIMIT 0;
                SELECT mount,id,presence FROM remote_presence LIMIT 0;
                SELECT mount,scope,path,json FROM remote_collections LIMIT 0;
                SELECT mount,scope,path,json FROM remote_shadow LIMIT 0;
                SELECT id,mount,scope,path,json FROM remote_pending LIMIT 0;
                SELECT id,mount,scope,state,started FROM remote_scans LIMIT 0;
                SELECT id,task,attempt,generation,state,started,finished,reason FROM operation_runs LIMIT 0;
                SELECT run,ordinal,name,state,started,finished,reason,completed,total,evidence FROM operation_steps LIMIT 0;
                SELECT id,mount,scope,connection,discovery,started,finished,error,hidden,cancel_requested FROM remote_task_batches LIMIT 0;
                SELECT pending,batch,ordinal,path,category,state,reason,action FROM remote_task_batch_members LIMIT 0;
                SELECT mount,scope,path,json FROM remote_temporary LIMIT 0;
                SELECT id,mount,scope,path,json FROM s3_event_hints LIMIT 0;
                SELECT mount,scope,path,sequence FROM s3_event_watermarks LIMIT 0;
                SELECT queue,id,received FROM sqs_messages LIMIT 0;
                """;
            try{using var schema=command.ExecuteReader();do{while(schema.Read()){} }while(schema.NextResult());}
            catch(SqliteException error)when(error.SqliteErrorCode==1){return new("Corrupt",version,HResult:error.HResult);}
            return new("Current",version);
        }
        catch(UnauthorizedAccessException error){return new("AccessDenied",HResult:error.HResult);}
        catch(SqliteException error){return new(error.SqliteErrorCode is 11 or 26?"Corrupt":"ReadError",HResult:error.HResult);}
        catch(IOException error){return new("ReadError",HResult:error.HResult);}
    }
    internal void RequireCurrent()
    {
        if(!CanStart)throw new MountIndexFormatException(this);
    }
}
/// <summary>格式拒绝只携带安全的分类及数字版本，不含数据库正文。</summary>
internal sealed class MountIndexFormatException(MountIndexFormat format):IOException($"挂载索引不可启动：{format.State}；格式={format.Version}。")
{
    internal MountIndexFormat Format {get;}=format;
}
