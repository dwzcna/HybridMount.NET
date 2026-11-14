using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HybridMount.Models.Diagnostics;
namespace HybridMount.Services;

/// <summary>早于WinUI的维护／Shell／解码宿主只写安全小记录；不创建Logger、托盘、挂载或公共管道。</summary>
internal static class HostDiagnosticStore
{
    internal sealed record Entry([property:JsonConverter(typeof(EvidenceIdConverter))] Guid Id,DateTimeOffset Utc,string Role,string Type,int HResult,int? ExitCode,string? ExpectedBuild=null,string? ActualBuild=null,HybridMount.Services.Runtime.NativeChildIdentity? ChildIdentity=null,string? Stage=null);
    /// <summary>仅转换宿主证据Id：兼容原生旧N格式及标准D格式；其他字段和公共协议继续使用各自转换规则。</summary>
    internal sealed class EvidenceIdConverter:JsonConverter<Guid>
    {
        /// <summary>供JsonConverter属性通过公共无参构造创建，不持有外部状态。</summary>
        public EvidenceIdConverter() { }
        /// <summary>在日志导入线程读取Id；只接受32／36位GUID字符串，无效值抛出JsonException，由导入方保留原文件。</summary>
        public override Guid Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)
        {
            if(reader.TokenType!=JsonTokenType.String)throw new JsonException("宿主证据标识必须为GUID字符串。");
            var value=reader.GetString();
            if(value is {Length:32}&&Guid.TryParseExact(value,"N",out var compact))return compact;
            if(value is {Length:36}&&Guid.TryParseExact(value,"D",out var standard))return standard;
            throw new JsonException("宿主证据标识格式无效。");
        }
        /// <summary>同步写出标准D格式；文件名仍由证据存储使用N格式，不修改调用方序列化选项。</summary>
        public override void Write(Utf8JsonWriter writer,Guid value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString("D"));
    }
    private static string? SafeBuild(string? build)=>build is {Length:>0 and <=64}&&build.All(c=>c is >= '0' and <= '9' or '.')?build:null;
    internal sealed record Captured(string Path,byte[] Bytes,Entry Value);
    /// <summary>按实际包身份定位同模式日志；仅用原生身份API，无WinRT应用初始化。</summary>
    private static string DirectoryPath()
    {
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);uint length=0;
        if(GetCurrentPackageFamilyName(ref length,null)==122&&length is >0 and <=1024)
        {
            var family=new StringBuilder((int)length);if(GetCurrentPackageFamilyName(ref length,family)==0)
                return Path.Combine(local,"Packages",family.ToString(),"LocalState","Logs","HostDiagnostics");
        }
        return Path.Combine(ApplicationPaths.PhysicalDataDirectory,"Logs","HostDiagnostics");
    }
    /// <summary>只记录角色、UTC、类型及代码，不保留参数、路径、异常消息或Data；写入失败不覆盖原退出结果。</summary>
    internal static bool Record(string role,Exception? error=null,int? exitCode=null,string? directory=null,string? stage=null)
    {
        if(role is not("ShellMaintenance" or "ShellServer" or "PreviewWorker" or "Startup" or "NativeRuntime"))return false;
        try
        {
            var path=directory??DirectoryPath();Directory.CreateDirectory(path);
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return false;
            var row=new Entry(Guid.NewGuid(),DateTimeOffset.UtcNow,role,error is null?"ExitCode":SafeDiagnostic.Field(error.GetType().Name),error?.HResult??0,exitCode,Stage:stage);
            var destination=Path.Combine(path,row.Id.ToString("N")+".json");var temporary=destination+".tmp";
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            {JsonSerializer.Serialize(stream,row);stream.Flush(true);}
            File.Move(temporary,destination);return true;
        }
        catch(Exception){return false;}
    }
    /// <summary>父进程直接记录本次子进程实际身份；固定在父应用日志目录，写入失败不改变启动结果。</summary>
#if HYBRIDMOUNT_HEADLESS_TEST
    internal static string? ChildIdentityDirectoryForTests {get;set;}
#endif
    internal static void RecordChildIdentity(string role,HybridMount.Services.Runtime.NativeChildIdentity proof)
    {
        try
        {
#if HYBRIDMOUNT_HEADLESS_TEST
            var path=ChildIdentityDirectoryForTests??Path.Combine(AppContext.BaseDirectory,"HostDiagnostics");
#else
            var path=Path.Combine(ApplicationPaths.LogsDirectory,"HostDiagnostics");
#endif
            Directory.CreateDirectory(path);
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||Directory.EnumerateFiles(path,"*.json").Take(256).Count()>=256)return;
            var row=new Entry(Guid.NewGuid(),DateTimeOffset.UtcNow,role,proof.Matches?"ChildIdentityVerified":"ChildIdentityMismatch",
                proof.Matches?0:unchecked((int)0x80070005),null,ChildIdentity:proof);
            var destination=Path.Combine(path,row.Id.ToString("N")+".json");var temporary=destination+".tmp";
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){JsonSerializer.Serialize(stream,row);stream.Flush(true);}
            File.Move(temporary,destination);
        }
        catch(Exception){ /* 诊断不能覆盖首个启动阶段的失败。 */ }
    }
    internal static int Run(string role,Func<int> action)
    {
        try{var result=action();if(result!=0&&role!="PreviewWorker")Record(role,exitCode:result);return result;}
        catch(Exception error){Record(role,error);return error.HResult<0?error.HResult:1;}
    }
    internal sealed record ReadResult(IReadOnlyList<Captured> Files,string? Failure);
    /// <summary>逐条核验；坏证据保留并报告，不阻断同目录其他有效事件。seen防止删除失败时反复读取同一条。</summary>
    internal static ReadResult Read(string directory,IReadOnlySet<Guid> seen)
    {
        if(!Directory.Exists(directory))return new([],null);
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("宿主证据目录不可为链接。");
        var result=new List<Captured>();string? failure=null;
        foreach(var path in Directory.EnumerateFiles(directory,"*.json").OrderBy(x=>x,StringComparer.Ordinal))
        {
            if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path),"N",out var id)||seen.Contains(id))continue;
            try
            {
                var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("宿主证据不能为链接。");
                using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                if(stream.Length>16384)throw new IOException("宿主证据超过安全上限。");
                var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
                var row=JsonSerializer.Deserialize<Entry>(bytes)??throw new IOException("宿主证据为空。");
                if(row.Id!=id||row.Role is not("ShellMaintenance" or "ShellServer" or "PreviewWorker" or "Startup" or "NativeRuntime"))throw new IOException("宿主证据身份无效。");
                result.Add(new(path,bytes,row with{Type=SafeDiagnostic.Field(row.Type),ExpectedBuild=SafeBuild(row.ExpectedBuild),ActualBuild=SafeBuild(row.ActualBuild)}));
                if(result.Count==256)break;
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {failure??=SafeDiagnostic.ExceptionSummary(error);}
        }
        return new(result,failure);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern int GetCurrentPackageFamilyName(ref uint length,StringBuilder? name);
}

public sealed partial class LoggerService
{
    /// <summary>串行导入早期宿主安全证据；数据库提交后才删除同一文件。崩溃后按GUID来源去重，清除先完成导入收尾。</summary>
    private async Task ImportHostsAsync(bool requireCleanup)
    {
        var directory=Path.Combine(Path.GetDirectoryName(_path)!,"HostDiagnostics");string? failure=null;
        try
        {
            var imported=0;var seen=new HashSet<Guid>();
            while(true)
            {
                var batch=HostDiagnosticStore.Read(directory,seen);failure??=batch.Failure;
                if(batch.Files.Count==0)break;
                if(imported+batch.Files.Count>10000)throw new IOException("早期宿主证据超过单次导入上限，请再次刷新；清除尚未执行。");
                imported+=batch.Files.Count;
                foreach(var captured in batch.Files)
                {
                    var row=captured.Value;seen.Add(row.Id);var source="后台宿主."+row.Id.ToString("N");
                    try
                    {
                        await using var db=await OpenAsync();await using var exists=db.CreateCommand();
                        exists.CommandText="SELECT 1 FROM diagnostic_logs WHERE owner_sid=$owner AND source=$source LIMIT 1";
                        exists.Parameters.AddWithValue("$owner",_ownerSid);exists.Parameters.AddWithValue("$source",source);
                        if(await exists.ExecuteScalarAsync() is null)
                        {
                            var message=$"角色={row.Role}；发生UTC={row.Utc:O}；类型={row.Type}；HRESULT=0x{row.HResult:X8}；退出码={row.ExitCode?.ToString()??"无"}";
                            if(row.ExpectedBuild is not null||row.ActualBuild is not null)message+=$"；预期版本={row.ExpectedBuild??"未知"}；实际版本={row.ActualBuild??"未知"}";
                            if(row.Stage is {} stage)message+=$"；启动阶段={SafeDiagnostic.Field(stage)}";
                            if(row.ChildIdentity is {} proof)message+=$"；包={proof.ExpectedPackage} -> {proof.ActualPackage}；映像={proof.ExpectedImage} -> {proof.ActualImage}；SID={proof.ExpectedUser} -> {proof.ActualUser}；会话={proof.ExpectedSession} -> {proof.ActualSession}";
                            foreach(var entry in await AppendBatchAsync([new(source,row.Type=="ChildIdentityVerified"?"Information":"Error",message)]))PublishEntry(entry);
                        }
                        if(File.Exists(captured.Path))
                        {
                            if(!File.ReadAllBytes(captured.Path).SequenceEqual(captured.Bytes))throw new IOException("宿主证据导入期间变化，保留原文件。");
                            File.Delete(captured.Path);
                        }
                    }
                    catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                    {failure??=SafeDiagnostic.ExceptionSummary(error);}
                }
            }
        }
        catch(Exception error){failure??=SafeDiagnostic.ExceptionSummary(error);}
        Volatile.Write(ref _hostError,failure is null?null:"早期宿主证据导入未完成；"+failure);
        if(requireCleanup&&failure is not null)throw new IOException("早期宿主证据仍未收尾，清除未执行；请查看日志存储状态。");
    }
}
