using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Persistence;

/// <summary>业务服务创建前的只读索引格式检查；旧格式不迁移，读取失败与较新格式分别报告。</summary>
internal sealed record MountIndexFormat(string State,int Version=0,int? HResult=null)
{
    internal const int CurrentVersion=7;
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
            db.Open();using var command=db.CreateCommand();command.CommandText="PRAGMA quick_check(1)";
            if(command.ExecuteScalar() as string!="ok")return new("Corrupt");
            command.CommandText="PRAGMA user_version";var version=Convert.ToInt32(command.ExecuteScalar());
            if(version<CurrentVersion)return new("Old",version);
            if(version>CurrentVersion)return new("Newer",version);
            command.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('entries','operations','task_intents','file_tasks','task_steps','task_attempts','task_outbox','intent_tasks','task_sequences','task_dependencies')";
            return Convert.ToInt32(command.ExecuteScalar())==10?new("Current",version):new("Corrupt",version);
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
