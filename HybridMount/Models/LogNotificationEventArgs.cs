using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Models;

/// <summary>日志通知事件的来源、正文和界面显示级别。</summary>
/// <param name="Source">日志来源。</param><param name="Message">日志正文。</param><param name="Severity">InfoBar 严重级别。</param>
public sealed record LogNotificationEventArgs(string Source, string Message, InfoBarSeverity Severity);
