using HybridMount.Commons.Diagnostics;
using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Models;
using HybridMount.Models.UI;
namespace HybridMount.Tests.Integration.Diagnostics;

/// <summary>后台日志通过纯数据和显式调度发布；存储不等待UI线程。</summary>
public sealed class DiagnosticBoundaryTests
{
    [Theory]
    [InlineData(PageMessageSeverity.Information, "Information")]
    [InlineData(PageMessageSeverity.Success, "Information")]
    [InlineData(PageMessageSeverity.Warning, "Warning")]
    [InlineData(PageMessageSeverity.Error, "Error")]
    public async Task NotificationWaitsForInjectedDispatcherWhileLogIsPersisted(PageMessageSeverity severity, string storedSeverity)
    {
        var directory = Path.Combine(Path.GetTempPath(), "HybridMount.LogBoundary", Guid.NewGuid().ToString("N"));
        var work = new Queue<Action>(); LogNotificationEventArgs? received = null;
        try
        {
            await using var logger = new LoggerService(Path.Combine(directory, "log.db"), notify: action => work.Enqueue(action));
            ILoggerService service = logger;
            service.NotificationRequested += (_, value) => received = value;
            await Task.Run(() => service.Log("fixture", "safe message", severity));
            var row = Assert.Single(await logger.ReadAsync(), x => x.Source == "fixture");
            Assert.Equal(storedSeverity, row.Severity); Assert.Null(received);
            Assert.Single(work); work.Dequeue()();
            Assert.Equal(severity, received!.Severity); Assert.Equal("safe message", received.Message);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
