using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Thumbnails;

/// <summary>可共享的根缓存授权，配置／身份变化递增epoch；数据库不保存凭据或源地址。</summary>
internal sealed record ThumbnailCacheRoot(Guid MountId,string Path,string FileId,string Owner,string Scope,bool Enabled,long Epoch=1);
/// <summary>缩略图绑定本机身份与内容代次；干净占位允许固定／脱水的元数据变化，修改内容使用精确戳。</summary>
internal sealed record ThumbnailCacheBinding(ThumbnailCacheRoot Root,string EntryId,string Path,string FileId,long Length,long LastWriteTime,string Stamp,string Revision,bool Clean,long Usn=0)
{
    internal string ContentKey=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Root.MountId:N}|{Root.Scope}|{Root.Epoch}|{EntryId}|{FileId}|{Revision}|v1")));
}
/// <summary>独立SQLite BLOB缓存。命中只读，不更新LRU；短事务、命名写入锁和WAL防止跨进程发布竞争。</summary>
internal sealed class ThumbnailCacheStore(string database)
{
    internal const int SchemaVersion=1;
    private static readonly byte[] Signature=[137,80,78,71,13,10,26,10];
    private readonly object _gate=new();
    internal string Database=>database;
    /// <summary>只读连接不建目录、不升级或修复库；竞争和损坏作为缓存缺失处理，默认等待至多一秒。</summary>
    private SqliteConnection Open(bool writable)
    {
        var parent=Path.GetDirectoryName(Path.GetFullPath(database))!;
        for(var current=new DirectoryInfo(parent);current is not null;current=current.Parent)
            if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("缩略图库路径经过重解析点。");
        if(File.Exists(database)&&(File.GetAttributes(database)&FileAttributes.ReparsePoint)!=0)throw new IOException("缩略图库不是普通文件。");
        if(writable)Directory.CreateDirectory(parent);
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=writable?SqliteOpenMode.ReadWriteCreate:SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());
        try{db.Open();using var command=db.CreateCommand();command.CommandText=writable?"PRAGMA busy_timeout=0;PRAGMA foreign_keys=ON;":"PRAGMA query_only=ON;PRAGMA busy_timeout=0;";command.ExecuteNonQuery();return db;}
        catch{db.Dispose();throw;}
    }
    /// <summary>只有写入者能创建表；读宿主拒绝较新或未知schema。</summary>
    private static void Initialize(SqliteConnection db)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";var version=Convert.ToInt32(cmd.ExecuteScalar());
        if(version==SchemaVersion)return;if(version!=0)throw new IOException("缩略图库版本不受支持，未修改原库。");
        cmd.CommandText="PRAGMA auto_vacuum=INCREMENTAL;PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;";cmd.ExecuteNonQuery();
        using var tx=db.BeginTransaction();cmd.Transaction=tx;
        cmd.CommandText="CREATE TABLE IF NOT EXISTS roots(mount TEXT PRIMARY KEY,json TEXT NOT NULL);CREATE TABLE IF NOT EXISTS bindings(mount TEXT NOT NULL,entry TEXT NOT NULL,path TEXT NOT NULL COLLATE NOCASE,content TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(mount,entry));CREATE INDEX IF NOT EXISTS binding_paths ON bindings(path);CREATE TABLE IF NOT EXISTS images(content TEXT NOT NULL,size INTEGER NOT NULL,png BLOB NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(content,size));PRAGMA user_version=1;";cmd.ExecuteNonQuery();tx.Commit();
    }
    private T Locked<T>(Func<T> action)
    {
        lock(_gate)
        {
            var name="Local\\HybridMount.ThumbnailCache."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(database).ToUpperInvariant())));
            using var mutex=new Mutex(false,name);var held=false;
            try{try{held=mutex.WaitOne(TimeSpan.FromSeconds(2));}catch(AbandonedMutexException){held=true;}if(!held)throw new IOException("缩略图库正忙，稍后重试。");return action();}
            finally{if(held)mutex.ReleaseMutex();}
        }
    }
    private T Write<T>(Func<SqliteConnection,T> action)=>Locked(()=>{using var db=Open(true);Initialize(db);return action(db);});
    /// <summary>旧共享库以一致只读快照迁移指定挂载；目标事务提交后才允许源库精确退休，绝不复制其他根。</summary>
    internal ThumbnailCacheRoot? CopyMountTo(ThumbnailCacheStore target,ThumbnailCacheRoot requested)
    {
        if(!File.Exists(database))return null;
        using var source=Open(false);if(!Supported(source))throw new IOException("旧缩略图库版本不受支持，保留原库。");
        using var snapshot=source.BeginTransaction(deferred:true);var root=ReadRoot(source,requested.MountId,snapshot);
        if(root is null||root.Owner!=requested.Owner||root.FileId!=requested.FileId||root.Path!=requested.Path||root.Scope!=requested.Scope)return null;
        var copied=target.Write(destination=>
        {
            using var tx=destination.BeginTransaction();EnsureSingleMount(destination,root.MountId,tx);if(ReadRoot(destination,root.MountId,tx) is not null)return false;
            using(var add=destination.CreateCommand()){add.Transaction=tx;add.CommandText="INSERT INTO roots VALUES($m,$j)";add.Parameters.AddWithValue("$m",root.MountId.ToString("N"));add.Parameters.AddWithValue("$j",JsonSerializer.Serialize(root));add.ExecuteNonQuery();}
            using(var rows=source.CreateCommand())
            {
                rows.Transaction=snapshot;rows.CommandText="SELECT entry,path,content,json FROM bindings WHERE mount=$m AND length(json)<=65536";rows.Parameters.AddWithValue("$m",root.MountId.ToString("N"));using var reader=rows.ExecuteReader();
                while(reader.Read())
                {
                    var binding=JsonSerializer.Deserialize<ThumbnailCacheBinding>(reader.GetString(3));if(binding?.Root!=root||binding.ContentKey!=reader.GetString(2))continue;
                    using var add=destination.CreateCommand();add.Transaction=tx;add.CommandText="INSERT INTO bindings VALUES($m,$e,$p,$c,$j)";add.Parameters.AddWithValue("$m",root.MountId.ToString("N"));add.Parameters.AddWithValue("$e",reader.GetString(0));add.Parameters.AddWithValue("$p",reader.GetString(1));add.Parameters.AddWithValue("$c",reader.GetString(2));add.Parameters.AddWithValue("$j",reader.GetString(3));add.ExecuteNonQuery();
                }
            }
            using(var rows=source.CreateCommand())
            {
                rows.Transaction=snapshot;rows.CommandText="SELECT content,size,png,created FROM images WHERE content IN (SELECT content FROM bindings WHERE mount=$m) AND length(png) BETWEEN 33 AND 393216";rows.Parameters.AddWithValue("$m",root.MountId.ToString("N"));using var reader=rows.ExecuteReader();
                while(reader.Read())
                {
                    var png=(byte[])reader[2];var size=reader.GetInt32(1);if(!ValidPng(png)||size is not(32 or 64 or 128 or 256))continue;
                    using var add=destination.CreateCommand();add.Transaction=tx;add.CommandText="INSERT INTO images VALUES($c,$s,$p,$t)";add.Parameters.AddWithValue("$c",reader.GetString(0));add.Parameters.AddWithValue("$s",size);add.Parameters.Add("$p",SqliteType.Blob).Value=png;add.Parameters.AddWithValue("$t",reader.GetInt64(3));add.ExecuteNonQuery();
                }
            }
            tx.Commit();return true;
        });return copied?root:null;
    }
    /// <summary>调用方已停止写入；同一命名锁内核对唯一根、正常检查点并关闭连接后精确删除库。读者占用或身份变化保留文件供重试。</summary>
    internal bool DeleteDatabase(ThumbnailCacheRoot? expected)=>Locked(()=>
    {
        if(!File.Exists(database))return true;
        using(var db=Open(false))
        {
            if(!Supported(db))throw new IOException("缩略图库版本未知，未删除。");using var command=db.CreateCommand();command.CommandText="SELECT count(*) FROM roots";var count=Convert.ToInt32(command.ExecuteScalar());
            if(expected is null?count!=0:count!=1||ReadRoot(db,expected.MountId)!=expected)throw new IOException("缩略图库身份变化或仍包含其他挂载，未删除。");
        }
        using(var db=Open(true)){using var command=db.CreateCommand();command.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";using var result=command.ExecuteReader();if(result.Read()&&result.GetInt32(0)!=0)throw new IOException("缩略图库仍在读取，稍后重试移除。");}
        // SQLite已关闭且不使用连接池；仅删除这一个库及同名日志，失败不扩大清理范围。
        foreach(var path in new[]{database+"-wal",database+"-shm",database})if(File.Exists(path))File.Delete(path);
        return true;
    });
    internal bool IsEmpty
    {get{if(!File.Exists(database))return true;using var db=Open(false);if(!Supported(db))return false;using var command=db.CreateCommand();command.CommandText="SELECT count(*) FROM roots";return Convert.ToInt32(command.ExecuteScalar())==0;}}
    private static ThumbnailCacheRoot? ReadRoot(SqliteConnection db,Guid mount,SqliteTransaction? tx=null)
    {using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT json FROM roots WHERE mount=$m AND length(json)<=65536";cmd.Parameters.AddWithValue("$m",mount.ToString("N"));return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<ThumbnailCacheRoot>(json):null;}
    /// <summary>改变启用、内容作用域、根路径或身份时撤销旧绑定；相同根重启保留epoch和图片。</summary>
    internal ThumbnailCacheRoot Configure(ThumbnailCacheRoot requested,bool singleMount=false)=>Write(db=>
    {
        using var tx=db.BeginTransaction();if(singleMount)EnsureSingleMount(db,requested.MountId,tx);var old=ReadRoot(db,requested.MountId,tx);var current=requested with{Epoch=old?.Epoch??1};
        if(old is not null&&old!=current)current=current with{Epoch=checked(old.Epoch+1)};
        if(old==current){tx.Commit();return current;}
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO roots VALUES($m,$j) ON CONFLICT(mount) DO UPDATE SET json=$j;DELETE FROM bindings WHERE mount=$m;";
        cmd.Parameters.AddWithValue("$m",requested.MountId.ToString("N"));cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(current));cmd.ExecuteNonQuery();tx.Commit();return current;
    });
    /// <summary>独立库不容纳第二个挂载或其孤立绑定；在写事务中核验，失败保留原库。</summary>
    private static void EnsureSingleMount(SqliteConnection db,Guid mount,SqliteTransaction tx)
    {using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="SELECT EXISTS(SELECT 1 FROM roots WHERE mount<>$m UNION ALL SELECT 1 FROM bindings WHERE mount<>$m)";command.Parameters.AddWithValue("$m",mount.ToString("N"));if(Convert.ToInt32(command.ExecuteScalar())!=0)throw new IOException("独立缩略图库含有其他挂载，未修改。 ");}
    internal ThumbnailCacheRoot? Root(Guid mount)
    {
        try{using var db=Open(false);if(!Supported(db))return null;return ReadRoot(db,mount);}
        catch(Exception error)when(Miss(error)){return null;}
    }
    private static bool Supported(SqliteConnection db){using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";return Convert.ToInt32(cmd.ExecuteScalar())==SchemaVersion;}
    /// <summary>发布先重验根epoch和启用状态；禁用／移除后的迟到结果不能恢复图片。</summary>
    internal bool Publish(ThumbnailCacheBinding binding,IReadOnlyDictionary<int,byte[]> frames)
    {
        if(frames.Count is <1 or >4||frames.Any(x=>x.Key is not(32 or 64 or 128 or 256)||!ValidPng(x.Value)))throw new IOException("缩略图帧无效。");
        return Write(db=>
        {
            using var tx=db.BeginTransaction();var root=ReadRoot(db,binding.Root.MountId,tx);if(root!=binding.Root||!root.Enabled){tx.Rollback();return false;}
            using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="DELETE FROM bindings WHERE path=$p AND mount<>$m;INSERT INTO bindings VALUES($m,$e,$p,$c,$j) ON CONFLICT(mount,entry) DO UPDATE SET path=$p,content=$c,json=$j";
            cmd.Parameters.AddWithValue("$m",root.MountId.ToString("N"));cmd.Parameters.AddWithValue("$e",binding.EntryId);cmd.Parameters.AddWithValue("$p",binding.Path);cmd.Parameters.AddWithValue("$c",binding.ContentKey);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(binding));cmd.ExecuteNonQuery();
            foreach(var frame in frames)
            {
                using var image=db.CreateCommand();image.Transaction=tx;image.CommandText="INSERT INTO images VALUES($c,$s,$b,$t) ON CONFLICT(content,size) DO UPDATE SET png=$b,created=$t";
                image.Parameters.AddWithValue("$c",binding.ContentKey);image.Parameters.AddWithValue("$s",frame.Key);image.Parameters.Add("$b",SqliteType.Blob).Value=frame.Value;image.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.UtcTicks);image.ExecuteNonQuery();
            }
            tx.Commit();return true;
        });
    }
    /// <summary>按请求选择最小足够帧，无更大帧时可返回已有较小图；不创建虚假的通用缩略图。</summary>
    internal byte[]? Get(ThumbnailCacheBinding binding,int size)=>Read(binding.Path,size,current=>current==binding);
    internal byte[]? Read(string path,int size,Func<ThumbnailCacheBinding,bool> validate)
    {
        try
        {
            using var db=Open(false);if(!Supported(db))return null;using var tx=db.BeginTransaction(deferred:true);
            using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT json FROM bindings WHERE path=$p AND length(json)<=65536 LIMIT 2";cmd.Parameters.AddWithValue("$p",path);
            ThumbnailCacheBinding binding;using(var rows=cmd.ExecuteReader()){if(!rows.Read())return null;binding=JsonSerializer.Deserialize<ThumbnailCacheBinding>(rows.GetString(0))??throw new IOException("空缩略图绑定。");if(rows.Read())return null;}
            if(ReadRoot(db,binding.Root.MountId,tx)!=binding.Root||!binding.Root.Enabled||!validate(binding))return null;
            using var image=db.CreateCommand();image.Transaction=tx;image.CommandText="SELECT png FROM images WHERE content=$c AND length(png) BETWEEN 33 AND 393216 ORDER BY (size<$s),CASE WHEN size>=$s THEN size ELSE -size END LIMIT 1";image.Parameters.AddWithValue("$c",binding.ContentKey);image.Parameters.AddWithValue("$s",Math.Clamp(size,1,256));
            if(image.ExecuteScalar() is not byte[] png||!ValidPng(png))return null;return png;
        }
        catch(Exception error)when(Miss(error)){return null;}
    }
    /// <summary>一个只读事务检查四种尺寸的头部及当前绑定；不重复打开库或加载完整BLOB。</summary>
    internal bool HasFrames(string path,Func<ThumbnailCacheBinding,bool> validate)
    {
        try
        {
            using var db=Open(false);if(!Supported(db))return false;using var tx=db.BeginTransaction(deferred:true);
            using var bindingQuery=db.CreateCommand();bindingQuery.Transaction=tx;bindingQuery.CommandText="SELECT json FROM bindings WHERE path=$p AND length(json)<=65536 LIMIT 2";bindingQuery.Parameters.AddWithValue("$p",path);
            ThumbnailCacheBinding binding;using(var rows=bindingQuery.ExecuteReader()){if(!rows.Read())return false;binding=JsonSerializer.Deserialize<ThumbnailCacheBinding>(rows.GetString(0))??throw new IOException("空缩略图绑定。");if(rows.Read())return false;}
            if(ReadRoot(db,binding.Root.MountId,tx)!=binding.Root||!binding.Root.Enabled||!validate(binding))return false;
            using var images=db.CreateCommand();images.Transaction=tx;images.CommandText="SELECT size,substr(png,1,24) FROM images WHERE content=$c AND size IN (32,64,128,256) AND length(png) BETWEEN 33 AND 393216";images.Parameters.AddWithValue("$c",binding.ContentKey);using var frames=images.ExecuteReader();var count=0;
            while(frames.Read()){var size=frames.GetInt32(0);var header=(byte[])frames[1];if(header.Length!=24||!header.AsSpan(0,8).SequenceEqual(Signature)||!header.AsSpan(12,4).SequenceEqual("IHDR"u8))return false;var width=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16,4));var height=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20,4));if(width<1||height<1||width>size||height>size)return false;++count;}
            return count==4;
        }
        catch(Exception error)when(Miss(error)){return false;}
    }
    /// <summary>取绑定元数据供本机校验，不解码或读取文件正文。</summary>
    internal ThumbnailCacheBinding? Binding(string path)
    {
        try{using var db=Open(false);if(!Supported(db))return null;using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM bindings WHERE path=$p AND length(json)<=65536 LIMIT 2";cmd.Parameters.AddWithValue("$p",path);using var rows=cmd.ExecuteReader();if(!rows.Read())return null;var result=JsonSerializer.Deserialize<ThumbnailCacheBinding>(rows.GetString(0));return rows.Read()?null:result;}
        catch(Exception error)when(Miss(error)){return null;}
    }
    /// <summary>仅确认的自有元数据变化可重绑戳；内容代次、根epoch、文件身份或旧记录变化时拒绝。</summary>
    internal bool Rebind(ThumbnailCacheBinding previous,ThumbnailCacheBinding current)=>Write(db=>
    {
        if(previous.ContentKey!=current.ContentKey||!previous.Clean||!current.Clean||previous.FileId!=current.FileId||previous.Length!=current.Length||previous.LastWriteTime!=current.LastWriteTime)return false;
        using var tx=db.BeginTransaction();if(ReadRoot(db,current.Root.MountId,tx)!=current.Root)return false;
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE bindings SET json=$j WHERE mount=$m AND entry=$e AND json=$old";cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(current));cmd.Parameters.AddWithValue("$old",JsonSerializer.Serialize(previous));cmd.Parameters.AddWithValue("$m",current.Root.MountId.ToString("N"));cmd.Parameters.AddWithValue("$e",current.EntryId);var accepted=cmd.ExecuteNonQuery()==1;tx.Commit();return accepted;
    });
    /// <summary>只有本产品当前缓存根下的条目失效；删除绑定即可使全部尺寸同时失效。</summary>
    internal void Invalidate(Guid mount,string? entry=null)=>Write(db=>{using var cmd=db.CreateCommand();cmd.CommandText=entry is null?"DELETE FROM bindings WHERE mount=$m":"DELETE FROM bindings WHERE mount=$m AND entry=$e";cmd.Parameters.AddWithValue("$m",mount.ToString("N"));cmd.Parameters.AddWithValue("$e",entry??"");return cmd.ExecuteNonQuery();});
    /// <summary>仅移除与调用者快照完全一致的根及其独占图像；并发重配置后拒绝清理，不影响其他挂载。</summary>
    internal bool RemoveRoot(ThumbnailCacheRoot expected)=>Write(db=>
    {
        using var tx=db.BeginTransaction();if(ReadRoot(db,expected.MountId,tx)!=expected){tx.Rollback();return false;}
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="DELETE FROM images WHERE content IN (SELECT content FROM bindings WHERE mount=$m) AND content NOT IN (SELECT content FROM bindings WHERE mount<>$m);DELETE FROM bindings WHERE mount=$m;DELETE FROM roots WHERE mount=$m";cmd.Parameters.AddWithValue("$m",expected.MountId.ToString("N"));cmd.ExecuteNonQuery();tx.Commit();return true;
    });
    internal long UsageBytes=>new[]{database,database+"-wal",database+"-shm"}.Sum(path=>{try{return LocalFileMetadata.TryRead(path)?.Length??0;}catch(IOException){return 0;}catch(UnauthorizedAccessException){return 0;}});
    /// <summary>后台按生成时间回收，依赖SQLite正常检查点与增量收缩，不手工删除正在使用的WAL。</summary>
    internal long Trim(long needed)=>Write(db=>
    {
        var before=UsageBytes;long removed=0;using(var tx=db.BeginTransaction())
        {
            var keys=new List<(string,int,long)>();using(var query=db.CreateCommand()){query.Transaction=tx;query.CommandText="SELECT content,size,length(png) FROM images ORDER BY created";using var rows=query.ExecuteReader();while(rows.Read()&&removed<needed){keys.Add((rows.GetString(0),rows.GetInt32(1),rows.GetInt64(2)));removed+=rows.GetInt64(2);}}
            foreach(var (key,size,_) in keys){using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="DELETE FROM images WHERE content=$c AND size=$s";cmd.Parameters.AddWithValue("$c",key);cmd.Parameters.AddWithValue("$s",size);cmd.ExecuteNonQuery();}
            using var clean=db.CreateCommand();clean.Transaction=tx;clean.CommandText="DELETE FROM bindings WHERE content NOT IN (SELECT DISTINCT content FROM images)";clean.ExecuteNonQuery();tx.Commit();
        }
        using var compact=db.CreateCommand();compact.CommandText="PRAGMA incremental_vacuum(1024);PRAGMA wal_checkpoint(TRUNCATE);";compact.ExecuteNonQuery();return Math.Max(0,before-UsageBytes);
    });
    internal static bool ValidPng(byte[] bytes)
    {
        if(bytes.Length is <33 or >ExePreviewCache.MaximumPngBytes||!bytes.AsSpan(0,8).SequenceEqual(Signature)||!bytes.AsSpan(12,4).SequenceEqual("IHDR"u8))return false;
        var width=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4));var height=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));return width is >0 and <=256&&height is >0 and <=256;
    }
    private static bool Miss(Exception error)=>error is SqliteException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException;
}
