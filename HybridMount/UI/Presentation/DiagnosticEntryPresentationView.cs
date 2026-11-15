using HybridMount.Models.Diagnostics;
using HybridMount.UI.Localization;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class DiagnosticEntryPresentationView(DiagnosticEntry value)
{
    private readonly DiagnosticEntry _value=value;
    private long Id => _value.Id;
    private string OwnerSid => _value.OwnerSid;
    private DateTimeOffset RecordedUtc => _value.RecordedUtc;
    private string Severity => _value.Severity;
    private string Source => _value.Source;
    private string Message => _value.Message;
    private string Origin => _value.Origin;
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
