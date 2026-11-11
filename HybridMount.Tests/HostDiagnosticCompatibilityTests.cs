using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Diagnostics;
using HybridMount.Services;
using Microsoft.Data.Sqlite;
namespace HybridMount.Tests;

/// <summary>隔离目录验证原生N／D格式证据兼容及导入保护；不访问现场日志或挂载。</summary>
[Collection("Native IPC")]
public sealed class HostDiagnosticCompatibilityTests
{
    /// <summary>每例独享临时SQLite及证据目录，释放时只清理本例创建的目录。</summary>
    private sealed class Area:IAsyncDisposable
    {
        internal string DirectoryPath {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HybridMount.HostDiagnosticTests",Guid.NewGuid().ToString("N"));
        internal string Path=>System.IO.Path.Combine(DirectoryPath,"logs.db");
        internal string EvidenceDirectory=>System.IO.Path.Combine(DirectoryPath,"HostDiagnostics");
        internal Area(){Directory.CreateDirectory(EvidenceDirectory);}
        public ValueTask DisposeAsync(){Directory.Delete(DirectoryPath,true);return ValueTask.CompletedTask;}
    }
    /// <summary>生成原生宿主实际字段形状，Id按参数使用历史N格式或标准D格式；所有内容均为测试数据。</summary>
    private static byte[] Payload(Guid id,string format,string type="RootIconLookup")=>JsonSerializer.SerializeToUtf8Bytes(new
    {
        Id=id.ToString(format),Utc="2026-11-10T04:20:45.522Z",Role="NativeRuntime",Type=type,
        HResult=unchecked((int)0x80070490),ExitCode=(int?)null
    });
    private static string EvidencePath(Area area,Guid id)=>System.IO.Path.Combine(area.EvidenceDirectory,id.ToString("N")+".json");

    /// <summary>重现现场256条历史记录；混合格式、重复读取和提交后重放不得抛出JSON异常或重复入库。</summary>
    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    [InlineData("Mixed")]
    public async Task Native_records_import_once_without_guid_exceptions(string format)
    {
        await using var area=new Area();var ids=Enumerable.Range(0,256).Select(_=>Guid.NewGuid()).ToArray();
        for(var index=0;index<ids.Length;index++)
            await File.WriteAllBytesAsync(EvidencePath(area,ids[index]),Payload(ids[index],format=="Mixed"?(index%2==0?"N":"D"):format));
        var exceptions=0;
        void Observe(object? sender,FirstChanceExceptionEventArgs args){if(args.Exception is JsonException)Interlocked.Increment(ref exceptions);}
        AppDomain.CurrentDomain.FirstChanceException+=Observe;
        try
        {
            await using(var logger=new LoggerService(area.Path))
            {
                var page=await logger.ReadAsync(new DiagnosticQuery("Error",Limit:500));
                Assert.Equal(256,page.Entries.Count);Assert.Equal(256,page.Entries.Select(row=>row.Source).Distinct().Count());
                Assert.All(page.Entries,row=>Assert.Contains("RootIconLookup",row.Message));Assert.Null(logger.LastError);
                Assert.Empty(Directory.GetFiles(area.EvidenceDirectory,"*.json"));
                // 同一GUID换一种文本格式，模拟提交后删除前崩溃；不能重复发布或入库。
                await File.WriteAllBytesAsync(EvidencePath(area,ids[0]),Payload(ids[0],format=="N"?"D":"N"));
                Assert.Equal(256,(await logger.ReadAsync(new DiagnosticQuery("Error",Limit:500))).Entries.Count);
                Assert.Empty(Directory.GetFiles(area.EvidenceDirectory,"*.json"));Assert.Null(logger.LastError);
            }
            await using var reopened=new LoggerService(area.Path);
            Assert.Equal(256,(await reopened.ReadAsync(new DiagnosticQuery("Error",Limit:500))).Entries.Count);
            await reopened.ClearAsync();Assert.Empty(await reopened.ReadAsync());
        }
        finally{AppDomain.CurrentDomain.FirstChanceException-=Observe;}
        Assert.Equal(0,Volatile.Read(ref exceptions));
    }

    /// <summary>仅证据Id兼容两种格式，重新序列化统一为D；其他Guid继续遵守默认JSON契约。</summary>
    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    public void Evidence_id_reads_both_formats_and_writes_standard_guid(string format)
    {
        var id=Guid.NewGuid();var row=Assert.IsType<HostDiagnosticStore.Entry>(JsonSerializer.Deserialize<HostDiagnosticStore.Entry>(Payload(id,format)));
        Assert.Equal(id,row.Id);Assert.Equal(id.ToString("D"),JsonSerializer.SerializeToElement(row).GetProperty("Id").GetString());
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<Guid>(JsonSerializer.Serialize(id.ToString("N"))));
    }

    /// <summary>无效Id的类型或格式不能进入兼容分支；原证据保留，其他有效记录仍可读取。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("17")]
    [InlineData("\"invalid-guid\"")]
    [InlineData("\"{015931c2-44ac-4f6b-9624-4ddd2ce2818f}\"")]
    public async Task Invalid_id_keeps_evidence_and_does_not_hide_valid_records(string token)
    {
        await using var area=new Area();var id=Guid.NewGuid();var bad=EvidencePath(area,id);
        var text=Encoding.UTF8.GetString(Payload(id,"N")).Replace("\""+id.ToString("N")+"\"",token,StringComparison.Ordinal);
        var bytes=Encoding.UTF8.GetBytes(text);await File.WriteAllBytesAsync(bad,bytes);
        var valid=Guid.NewGuid();await File.WriteAllBytesAsync(EvidencePath(area,valid),Payload(valid,"D"));
        var result=HostDiagnosticStore.Read(area.EvidenceDirectory,new HashSet<Guid>());
        Assert.NotNull(result.Failure);Assert.Equal(valid,Assert.Single(result.Files).Value.Id);Assert.Equal(bytes,await File.ReadAllBytesAsync(bad));
    }

    /// <summary>兼容转换不得放宽文件名和正文Id的一致性校验。</summary>
    [Fact]
    public async Task Mismatched_filename_is_retained()
    {
        await using var area=new Area();var path=EvidencePath(area,Guid.NewGuid());var bytes=Payload(Guid.NewGuid(),"N");
        await File.WriteAllBytesAsync(path,bytes);var result=HostDiagnosticStore.Read(area.EvidenceDirectory,new HashSet<Guid>());
        Assert.Empty(result.Files);Assert.NotNull(result.Failure);Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
    }

    /// <summary>实际SQLite插入失败时保留证据，解除失败后允许再次导入且只保存一条。</summary>
    [Fact]
    public async Task Failed_database_insert_keeps_evidence_for_retry()
    {
        await using var area=new Area();await using var logger=new LoggerService(area.Path);await logger.ReadAsync();
        await ExecuteSql(area.Path,"CREATE TRIGGER fail_host_insert BEFORE INSERT ON diagnostic_logs BEGIN SELECT RAISE(ABORT,'injected'); END");
        var id=Guid.NewGuid();var path=EvidencePath(area,id);var bytes=Payload(id,"N");await File.WriteAllBytesAsync(path,bytes);
        Assert.Empty(await logger.ReadAsync());Assert.NotNull(logger.LastError);Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
        await ExecuteSql(area.Path,"DROP TRIGGER fail_host_insert");
        Assert.Single(await logger.ReadAsync());Assert.False(File.Exists(path));Assert.Null(logger.LastError);
    }

    /// <summary>在已提交通知中改变证据内容，验证删除前重验屏障确实保留变化后的文件。</summary>
    [Fact]
    public async Task Changed_evidence_is_not_deleted_after_database_commit()
    {
        await using var area=new Area();var id=Guid.NewGuid();var path=EvidencePath(area,id);
        await File.WriteAllBytesAsync(path,Payload(id,"N"));var changed=Payload(id,"D","ChangedAfterCommit");var calls=0;
        await using var logger=new LoggerService(area.Path);
        logger.EntryWritten+=(_,row)=>{if(row.Source=="后台宿主."+id.ToString("N")){File.WriteAllBytes(path,changed);Interlocked.Increment(ref calls);}};
        Assert.Single(await logger.ReadAsync());Assert.Equal(1,calls);Assert.Equal(changed,await File.ReadAllBytesAsync(path));Assert.NotNull(logger.LastError);
    }

    /// <summary>仅向隔离测试库设置／移除固定失败触发器；不改变生产数据库结构。</summary>
    private static async Task ExecuteSql(string path,string sql)
    {
        await using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
        await db.OpenAsync();await using var command=db.CreateCommand();command.CommandText=sql;await command.ExecuteNonQueryAsync();
    }
}
