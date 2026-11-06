using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>问题独立于清除历史，持久覆盖重试与重启；不将普通等待、取消或已完成误报为失败。</summary>
public sealed class UploadAttentionTests
{
    private static LocalUpload Item(string state,string? error=null)=>new("test",Guid.NewGuid(),"file.txt","file.txt",false,"id:1:1:1:1:False",1,state,DateTimeOffset.UtcNow,Error:error);
    [Theory]
    [InlineData("Failed")][InlineData("Conflict")][InlineData("OutcomeUnknown")][InlineData("WaitingCapacity")][InlineData("RemoteCommitted")]
    public void BlockingStatesKeepReasonThroughRetryAndResolveOnCompletion(string state)
    {
        var item=UploadAttentionRules.Normalize(Item(state,"specific reason"));Assert.True(UploadAttentionRules.NeedsAttention(item));var original=item.Attention!;
        item=UploadAttentionRules.Normalize(item with{State="Queued",Error=null});Assert.Equal(original.Reason,item.Attention!.Reason);Assert.Contains("正在重试",item.ToSnapshot().DisplayState);
        item=UploadAttentionRules.Normalize(item with{State="Sending"});Assert.Equal(original.OccurredUtc,item.Attention!.OccurredUtc);
        item=UploadAttentionRules.Normalize(item with{State="Completed"});Assert.False(UploadAttentionRules.NeedsAttention(item));Assert.Equal("ClearPending",item.Attention!.ShellState);Assert.Empty(item.ToSnapshot().IssueStage);Assert.Empty(item.ToSnapshot().IssueTimeText);
    }
    [Theory]
    [InlineData("Queued")][InlineData("Waiting")][InlineData("Prepared")][InlineData("Sending")][InlineData("Canceled")][InlineData("Completed")]
    public void NormalStatesDoNotCreateAttention(string state)=>Assert.Null(UploadAttentionRules.Normalize(Item(state)).Attention);
    [Fact]
    public void ExplicitCancellationResolvesButNewFailureCreatesNewEpisode()
    {
        var a=UploadAttentionRules.Normalize(Item("Failed","first"));var key=a.Attention!.NotificationKey;
        var canceled=UploadAttentionRules.Normalize(a with{State="Canceled"});Assert.False(UploadAttentionRules.NeedsAttention(canceled));
        var next=UploadAttentionRules.Normalize(canceled with{State="Failed",Error="again",Generation=2});Assert.True(UploadAttentionRules.NeedsAttention(next));Assert.NotEqual(key,next.Attention!.NotificationKey);
    }
    [Fact]
    public void LocalCommitFailureIsNotDisplayedAsRetryAndAnotherFailureCreatesANewEpisode()
    {
        var item=UploadAttentionRules.Normalize(Item("RemoteCommitted","local commit failed"));var first=item.Attention!.NotificationKey;
        Assert.DoesNotContain("正在重试",item.ToSnapshot().DisplayState);
        item=UploadAttentionRules.Normalize(item with{Error=null,Attention=item.Attention with{RetryRequested=true}});Assert.Contains("正在重试",item.ToSnapshot().DisplayState);
        item=UploadAttentionRules.Normalize(item with{Error="local commit failed"});Assert.NotEqual(first,item.Attention!.NotificationKey);Assert.DoesNotContain("正在重试",item.ToSnapshot().DisplayState);
    }
    [Fact]
    public void CompletingOneCopyDoesNotHideAnotherIssueForTheSameFile()
    {
        var a=UploadAttentionRules.Normalize(Item("Conflict","original conflict"));
        var b=UploadAttentionRules.Normalize(a with{Id="copy",SourcePath="copy.txt",Attention=null,State="Failed",Error="copy failed"});
        Assert.Contains("2 个同步问题",UploadAttentionRules.ShellText([a,b],a.LocalPath,"id"));
        b=UploadAttentionRules.Normalize(b with{State="Completed"});Assert.Equal("original conflict",UploadAttentionRules.ShellText([a,b],a.LocalPath,"id"));
        a=UploadAttentionRules.Normalize(a with{State="Canceled"});Assert.Empty(UploadAttentionRules.ShellText([a,b],a.LocalPath,"id"));
    }
    [Fact(Timeout=60000)]
    public async Task ClearedFailedHistoryStillHasIssueAndRestartedRetryKeepsIt()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"attention.txt");await File.WriteAllTextAsync(path,"local only");await test.Manager.ScanUploadChangesAsync(test.Id);
        var upload=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="attention.txt");test.Repository.UpdateUpload(upload.Id,x=>x with{State="Failed",Error="server refused"});
        test.Repository.SaveOperation(test.Repository.Upload(upload.Id)!.ToSnapshot());Assert.Single(test.Manager.UploadIssues);
        Assert.Equal(1,await test.Manager.ClearOperationHistoryAsync(TaskHistoryClearMode.Failed));Assert.DoesNotContain(test.Manager.Operations,x=>x.Id==upload.Id);Assert.Single(test.Manager.UploadIssues);
        Assert.True(test.Manager.RetryUpload(upload.Id));Assert.Contains("正在重试",Assert.Single(test.Manager.UploadIssues).DisplayState);
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);Assert.Single(test.Manager.UploadIssues);Assert.DoesNotContain(test.Repository.Entries(test.Id),x=>x.LocalPath=="attention.txt");
        test.Repository.UpdateUpload(upload.Id,x=>x with{State="Canceled"});Assert.Empty(test.Manager.UploadIssues);
    }
}
