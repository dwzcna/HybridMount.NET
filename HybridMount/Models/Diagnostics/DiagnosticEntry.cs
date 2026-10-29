namespace HybridMount.Models.Diagnostics;
/// <summary>已持久化诊断日志的一条记录。</summary>
/// <remarks>Origin 用于区分历史迁移数据与当前 Backend 产生的日志来源。</remarks>
public sealed record DiagnosticEntry(long Id, string OwnerSid, DateTimeOffset RecordedUtc, string Severity, string Source, string Message, string Origin = "Legacy")
{
    /// <summary>将 UTC 记录时间转换为本地 24 小时制日期时间文本。</summary>
    public string DisplayTime => RecordedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>将持久化级别转换成诊断页使用的单字母标记。</summary>
    /// <remarks>仅改变显示值；Severity 原文仍用于数据库和 IPC 契约。</remarks>
    public string DisplaySeverity => Severity switch
    {
        "Information" => "I",
        "Warning" => "W",
        "Error" => "E",
        _ => Severity
    };

    /// <summary>组合诊断页显示及单击复制共用的完整日志行。</summary>
    public string DisplayText => $"[{DisplaySeverity}]{DisplayTime} [{Source}] {Message}";
}
