using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Commons;

/// <summary>统一写入日志并向界面发布日志通知。</summary>
public interface ILoggerService
{
    /// <summary>界面通知请求事件。</summary>
    event EventHandler<LogNotificationEventArgs>? NotificationRequested;
    /// <summary>记录一条日志。</summary><param name="source">日志来源。</param><param name="message">日志正文。</param><param name="severity">显示严重级别。</param>
    void Log(string source, string message, InfoBarSeverity severity = InfoBarSeverity.Informational);
}
