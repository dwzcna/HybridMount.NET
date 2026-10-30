using System.Text.Json;
namespace HybridMount.Tests;

/// <summary>生命周期证据的敏感信息边界、会话完整性及并发写入回归。</summary>
public sealed class LifecycleDiagnosticsTests
{
    [Fact]
    public async Task ExceptionEvidenceExcludesMessagesAndDataButRetainsTypeAndStack()
    {
        await using var fixture=new DavFixture();var diagnostics=new LifecycleDiagnostics(Path.Combine(fixture.Area,"logs"),"0.0.4.4","Unpackaged");
        try{throw new InvalidOperationException("secret-password-and-auth-body");}
        catch(Exception error){error.Data["Authorization"]="Bearer private-token";Assert.True(diagnostics.Record("UiUnhandled",error,true));}
        var evidence=File.ReadAllText(diagnostics.EventFilePath);Assert.DoesNotContain("secret-password",evidence);Assert.DoesNotContain("private-token",evidence);
        using var item=JsonDocument.Parse(File.ReadLines(diagnostics.EventFilePath).Last());var exception=item.RootElement.GetProperty("exceptions")[0];
        Assert.Equal(typeof(InvalidOperationException).FullName,exception.GetProperty("type").GetString());Assert.Contains(nameof(ExceptionEvidenceExcludesMessagesAndDataButRetainsTypeAndStack),exception.GetProperty("stack").GetString());Assert.True(item.RootElement.GetProperty("fatal").GetBoolean());
    }
    [Fact]
    public async Task IncompleteAndCompletedSessionsAreDistinguishedWithoutClaimingCrash()
    {
        await using var fixture=new DavFixture();var logs=Path.Combine(fixture.Area,"logs");var first=new LifecycleDiagnostics(logs,"0.0.4.4","Packaged");Assert.False(first.PreviousSessionIncomplete);first.Record("Ready");
        var second=new LifecycleDiagnostics(logs,"0.0.4.4","Packaged");Assert.True(second.PreviousSessionIncomplete);Assert.Contains("PreviousSessionIncomplete",File.ReadAllText(second.EventFilePath));second.Record("ExitRequested");second.Record("ExitCompleted",completed:true);
        var third=new LifecycleDiagnostics(logs,"0.0.4.4","Packaged");Assert.False(third.PreviousSessionIncomplete);third.Record("ExitCompleted",completed:true);
    }
    [Fact]
    public async Task ConcurrentEvidenceRemainsValidAndIoFailureDoesNotMaskOriginalFailure()
    {
        await using var fixture=new DavFixture();var diagnostics=new LifecycleDiagnostics(Path.Combine(fixture.Area,"logs"),"0.0.4.4","Unpackaged");
        Parallel.For(0,100,index=>Assert.True(diagnostics.Record("Stage-"+index)));foreach(var line in File.ReadLines(diagnostics.EventFilePath)){using var json=JsonDocument.Parse(line);Assert.Equal("0.0.4.4",json.RootElement.GetProperty("version").GetString());}
        var blocked=Path.Combine(fixture.Area,"not-a-directory");await File.WriteAllTextAsync(blocked,"occupied");var unavailable=new LifecycleDiagnostics(blocked,"0.0.4.4","Unpackaged");Assert.False(unavailable.Record("UiUnhandled",new Exception("private-body"),true));Assert.NotNull(unavailable.LastWriteError);
    }
}
