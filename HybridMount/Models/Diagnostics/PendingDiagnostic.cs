namespace HybridMount.Models.Diagnostics;
/// <summary>尚未持久化或发送的诊断日志。</summary>
public sealed record PendingDiagnostic(string Source, string Severity, string Message);
