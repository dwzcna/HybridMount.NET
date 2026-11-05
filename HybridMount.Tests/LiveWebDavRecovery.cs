using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.Data.Sqlite;

namespace HybridMount.Tests;

/// <summary>仅恢复已停止且本地同步根已移除的显式 DAV 测试；使用公开 API，不修改生产清理保护策略。</summary>
public static class LiveWebDavRecovery
{
    /// <summary>核对唯一根、完整清单、临时项作用域、摘要及强标签后清理；未知或变化内容直接拒绝。</summary>
    public static async Task<string> RecoverAsync(string workDirectory,CancellationToken token=default)
    {
        var work=Path.GetFullPath(workDirectory);using var document=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work,"live-report.json"),token));var report=document.RootElement;
        var id=report.GetProperty("RunId").GetGuid();var remote=report.GetProperty("RemoteRoot").GetString();
        if(report.GetProperty("Phase").GetString() is not("Completed" or "FocusedCompleted" or "CompletedWithFailures" or "StoppedBeforeRequestedDuration" or "Canceled")||report.GetProperty("EndedUtc").ValueKind==JsonValueKind.Null)
            throw new IOException("测试尚未进入已结束状态，拒绝并发恢复清理。");
        if(remote!="hm-validation-0053-"+id.ToString("N")||report.GetProperty("StartedUtc").ValueKind==JsonValueKind.Null)throw new IOException("测试根没有本轮创建证据。");
        if(MountPathGuard.ReadRoot(Path.Combine(work,"mount")) is not null)throw new IOException("本地测试根尚未移除，拒绝远端恢复清理。");
        var credentials=new CredentialStore(work).Load(id);var endpoint=FileSourceFactory.Endpoint(new(FileSourceKind.WebDav,report.GetProperty("Endpoint").GetString()!,AllowHttp:true)).AbsoluteUri;
        var root=endpoint+remote+"/";var registry=@"Software\HybridMount.NET.Tests\LiveDav-"+id.ToString("N");var repository=new MountRepository(Path.Combine(work,"data"),registry);
        if(repository.LoadDefinitions().Count!=0)throw new IOException("测试挂载定义仍存在，先通过正常移除流程处理。");
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,root,AllowHttp:true),credentials,new WholeFileCache(Path.Combine(work,"recovery-cache")));
        await using var parent=new WebDavFileSource(new(FileSourceKind.WebDav,endpoint,AllowHttp:true),credentials,new WholeFileCache(Path.Combine(work,"recovery-parent")));
        using var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};
        http.DefaultRequestHeaders.Authorization=new("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName+":"+credentials.Password)));
        var known=report.GetProperty("KnownPaths").EnumerateArray().Select(x=>RelativePaths.Normalize(x.GetString()!)).ToHashSet(StringComparer.Ordinal);
        var inventory=new List<SourceEntry>();async Task Collect(string path){await foreach(var entry in source.ListAsync(path,token)){inventory.Add(entry);if(entry.IsDirectory)await Collect(entry.Path);}}
        await Collect("");var journal=new Dictionary<string,RemoteTemporaryItem>(StringComparer.Ordinal);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(work,"data","mounts.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {
            db.Open();using var query=db.CreateCommand();query.CommandText="SELECT json FROM remote_temporary";using var rows=query.ExecuteReader();
            while(rows.Read())
            {
                var item=JsonSerializer.Deserialize<RemoteTemporaryItem>(rows.GetString(0))??throw new IOException("临时记录损坏。");
                foreach(var prefix in new[]{"","mount/"})if(item.Scope==Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root+prefix+"\0"+credentials.UserName)))&&item.Purpose=="probe"&&Regex.IsMatch(item.Path,@"^\.?hybridmount-probe-[a-f0-9]{32}$"))journal[prefix+item.Path]=item;
            }
        }
        if(inventory.Any(x=>!known.Contains(x.Path)&&!journal.ContainsKey(x.Path)))throw new IOException("完整枚举包含未登记的远端项，未删除任何内容。");
        var tags=new Dictionary<string,EntityTagHeaderValue>(StringComparer.Ordinal);var verified=new List<string>();
        // 先验证全部探测内容，再开始删除，任何外部变化都保留整个现场。
        foreach(var entry in inventory.Where(x=>journal.ContainsKey(x.Path)))
        {
            if(entry.IsDirectory||entry.Size>4096)throw new IOException("记录的探测对象类型或长度异常。");
            using var response=await http.GetAsync(Address(entry.Path),token);if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("探测对象读取未确认。");
            var bytes=await response.Content.ReadAsByteArrayAsync(token);var tag=response.Headers.ETag;
            if(bytes.Length!=entry.Size||!journal[entry.Path].Hashes.Contains(Convert.ToHexString(SHA256.HashData(bytes)),StringComparer.Ordinal)||tag is null||tag.IsWeak||tag.Tag=="*")throw new IOException("探测对象内容或可靠版本不同，保留现场。");
            tags[entry.Path]=tag;verified.Add(entry.Path);
        }
        foreach(var entry in inventory.OrderByDescending(x=>x.Path.Count(c=>c=='/')).ThenBy(x=>x.IsDirectory?1:0))
        {
            token.ThrowIfCancellationRequested();if(entry.IsDirectory)await RequireEmpty(entry.Path);
            using var request=new HttpRequestMessage(HttpMethod.Delete,Address(entry.Path,entry.IsDirectory));
            if(tags.TryGetValue(entry.Path,out var tag))request.Headers.IfMatch.Add(tag);
            using var response=await http.SendAsync(request,token);if(!response.IsSuccessStatusCode&&response.StatusCode!=HttpStatusCode.NotFound)throw new IOException("本轮精确删除未确认："+entry.Path);
            if(journal.TryGetValue(entry.Path,out var item))repository.CompleteTemporary(item.MountId,item.Scope,item.Path);
        }
        await RequireEmpty("");using(var request=new HttpRequestMessage(HttpMethod.Delete,root))using(var response=await http.SendAsync(request,token))if(!response.IsSuccessStatusCode&&response.StatusCode!=HttpStatusCode.NotFound)throw new IOException("测试根删除未确认。");
        if(await parent.TryGetMetadataAsync(remote!,token) is not null)throw new IOException("测试根仍存在。");
        var output=Path.Combine(work,"recovery-cleanup.json");await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Version="0.0.5.3",CheckedUtc=DateTimeOffset.UtcNow,RunId=id,RootRemoved=true,LocalRootUnregistered=true,OriginalFailureReportPreserved=true,VerifiedProbePaths=verified,DeletedPaths=inventory.Select(x=>x.Path).ToArray()},new JsonSerializerOptions{WriteIndented=true}),token);return output;
        Uri Address(string path,bool directory=false)=>new(root+string.Join('/',path.Split('/').Select(Uri.EscapeDataString))+(directory?"/":""));
        async Task RequireEmpty(string path){await foreach(var _ in source.ListAsync(path,token))throw new IOException("目录仍有内容，拒绝递归删除："+path);}
    }
}
