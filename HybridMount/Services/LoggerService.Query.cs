using System.Runtime.InteropServices;
using System.Reflection;
using HybridMount.Models.Diagnostics;
using Microsoft.Data.Sqlite;
namespace HybridMount.Services;

public sealed partial class LoggerService
{
    /// <summary>查询所有仍保留记录中的一页；读取排在此前写入后，取消等待不破坏队列屏障。</summary>
    public async Task<DiagnosticPage> ReadAsync(DiagnosticQuery query,CancellationToken token=default)
    {
        var validated=query.Validate();var completion=new TaskCompletionSource<DiagnosticPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _operations.Writer.WriteAsync(new Query(validated,completion),token).ConfigureAwait(false);
        return await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }
    /// <summary>导出完整筛选范围，忽略视口游标，单次串行读冻结边界；最多现有保留容量。</summary>
    public async Task<DiagnosticExport> ExportAsync(DiagnosticQuery query,CancellationToken token=default)
    {
        var validated=query.Validate() with{BeforeId=null};var completion=new TaskCompletionSource<DiagnosticPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _operations.Writer.WriteAsync(new Query(validated,completion,true),token).ConfigureAwait(false);
        var page=await completion.Task.WaitAsync(token).ConfigureAwait(false);
        var version=typeof(LoggerService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        var text=$"HybridMount.NET {version}；架构={RuntimeInformation.ProcessArchitecture}；系统={Environment.OSVersion.Version}\n"+
            $"导出UTC={DateTimeOffset.UtcNow:O}；截止ID={page.ThroughId}；匹配={page.Entries.Count}\n"+
            $"级别={validated.Severity}；搜索={(validated.Text.Length==0?"无":$"已启用（{validated.Text.Length}字符）")}；UTC范围=[{validated.FromUtc?.ToString("O")??"不限"}, {validated.ToUtc?.ToString("O")??"不限"})\n"+
            $"保留规则：最近30天／最多10000条；过期和已清除日志不在范围内；截断={page.HasMore}；本会话未保存={DroppedCount}\n\n"+
            string.Join(Environment.NewLine,page.Entries.Select(x=>x.DisplayText));
        return new(text,page.Entries.Count,page.ThroughId);
    }
    private async Task<DiagnosticPage> QueryCoreAsync(DiagnosticQuery query,bool export)
    {
        await using var db=await OpenAsync();
        await using var maximum=db.CreateCommand();maximum.CommandText="SELECT COALESCE(MAX(id),0) FROM diagnostic_logs";
        var through=query.ThroughId??Convert.ToInt64(await maximum.ExecuteScalarAsync());var limit=export?10000:query.Limit;
        await using var command=db.CreateCommand();
        command.CommandText="""
            SELECT id,owner_sid,recorded_utc,severity,source,message FROM diagnostic_logs
            WHERE owner_sid=$owner AND id<=$through AND ($before IS NULL OR id<$before)
              AND ($severity='All' OR severity=$severity)
              AND ($from IS NULL OR recorded_utc>=$from) AND ($to IS NULL OR recorded_utc<$to)
              AND ($text='' OR hm_contains(source || char(10) || message,$text))
            ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$owner",_ownerSid);command.Parameters.AddWithValue("$through",through);
        command.Parameters.AddWithValue("$before",(object?)query.BeforeId??DBNull.Value);command.Parameters.AddWithValue("$severity",query.Severity);
        command.Parameters.AddWithValue("$from",(object?)query.FromUtc?.ToString("O")??DBNull.Value);command.Parameters.AddWithValue("$to",(object?)query.ToUtc?.ToString("O")??DBNull.Value);
        command.Parameters.AddWithValue("$text",query.Text);command.Parameters.AddWithValue("$limit",limit+1);
        var entries=new List<DiagnosticEntry>();await using var reader=await command.ExecuteReaderAsync();
        while(await reader.ReadAsync())entries.Add(new(reader.GetInt64(0),reader.GetString(1),DateTimeOffset.Parse(reader.GetString(2)),reader.GetString(3),reader.GetString(4),reader.GetString(5),"App"));
        var more=entries.Count>limit;if(more)entries.RemoveAt(entries.Count-1);
        return new(entries,through,more?entries[^1].Id:null,more);
    }
    private void RecordDropped(int count){Interlocked.Add(ref _dropped,count);Interlocked.Add(ref _pendingDropped,count);}
    /// <summary>直接写摘要，不重新入队；落库成功才扣除待报告数量，存储失败和退出保留真实丢失数。</summary>
    private async Task ReportDroppedAsync()
    {
        var count=Interlocked.Read(ref _pendingDropped);if(count==0)return;
        try
        {
            var rows=await AppendBatchAsync([new("日志存储","Warning",$"有 {count} 条诊断未保存（队列满、关闭或存储失败）；本会话累计 {DroppedCount} 条。")]);
            Interlocked.Add(ref _pendingDropped,-count);
            foreach(var entry in rows)PublishEntry(entry);
        }
        catch(Exception error){Volatile.Write(ref _lastError,SafeDiagnostic.ExceptionSummary(error));}
    }
}
