namespace HybridMount.Models.Diagnostics;

/// <summary>持久日志查询；UTC结束时间不包含边界，游标固定本次快照，不拼接SQL。</summary>
public sealed record DiagnosticQuery(string Severity="All",string Text="",DateTimeOffset? FromUtc=null,DateTimeOffset? ToUtc=null,long? BeforeId=null,long? ThroughId=null,int Limit=100)
{
    internal DiagnosticQuery Validate()
    {
        if(Severity is not("All" or "Information" or "Warning" or "Error")||Limit is <1 or >500||Text is null||Text.Length>1024||BeforeId<0||ThroughId<0||FromUtc>ToUtc)
            throw new ArgumentException("日志筛选范围无效。");
        return this with{Text=Text.Trim(),FromUtc=FromUtc?.ToUniversalTime(),ToUtc=ToUtc?.ToUniversalTime()};
    }
    internal bool Matches(DiagnosticEntry entry)=>(Severity=="All"||entry.Severity==Severity)&&
        (FromUtc is null||entry.RecordedUtc>=FromUtc)&&(ToUtc is null||entry.RecordedUtc<ToUtc)&&
        (Text.Length==0||(entry.Source+"\n"+entry.Message).Contains(Text,StringComparison.OrdinalIgnoreCase));
}
/// <summary>倒序分页结果；NextBeforeId为下一页排他游标，ThroughId在后续读取期间保持。</summary>
public sealed record DiagnosticPage(IReadOnlyList<DiagnosticEntry> Entries,long ThroughId,long? NextBeforeId,bool HasMore);
/// <summary>完整筛选导出及其固定边界；不包含凭据、配置或正文备份。</summary>
public sealed record DiagnosticExport(string Text,int Count,long ThroughId);
