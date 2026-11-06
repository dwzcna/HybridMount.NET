using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class WebDavTemporaryCleanupTests
{
    private static readonly DateTimeOffset Modified=new(2026,10,29,12,0,0,TimeSpan.Zero);
    private static (MountRepository Repository,MountDefinition Mount,RemoteTemporaryItem Item) Owned(DavFixture fixture,byte[]? bytes=null)
    {
        var repository=new MountRepository(Path.Combine(fixture.Area,"journal"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));var mount=new MountDefinition(Guid.NewGuid(),"元数据清理回归",fixture.Area,Source:fixture.Options);
        var path="hybridmount-upload-"+Guid.NewGuid().ToString("N");bytes??=[17,31];fixture.Put(path,bytes);
        var scope=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Options.Endpoint+"\0user")));var item=new RemoteTemporaryItem(mount.Id,scope,path,"upload","Owned",[Convert.ToHexString(SHA256.HashData(bytes))]);repository.SaveTemporary(item);return(repository,mount,item);
    }
    [Fact]
    public async Task FiftyMegabyteMetadataMatchDeletesConditionallyWithoutGet()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var tag=fixture.Objects[owned.Item.Path].ETag;const long size=50L*1024*1024;owned.Repository.SaveTemporary(owned.Item with{ETag=tag,Size=size,ModifiedUtc=Modified});var handle=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            var response=await handle(request);if(request.Method!="PROPFIND"||response.Status!=207)return response;
            var xml=XElement.Parse(Encoding.UTF8.GetString(response.Body));foreach(var value in xml.Descendants(XName.Get("getcontentlength","DAV:")))value.Value=size.ToString();return response with{Body=Encoding.UTF8.GetBytes(xml.ToString())};
        };
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();
        Assert.Empty(source.PendingTemporaryItems);Assert.False(fixture.Objects.ContainsKey(owned.Item.Path));Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="GET");Assert.Equal(tag,Assert.Single(fixture.Server.Requests,r=>r.Method=="DELETE").Headers["If-Match"]);
    }
    [Fact]
    public async Task RecordedStrongTagMismatchNeverFallsBackToHashOrDeletes()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);owned.Repository.SaveTemporary(owned.Item with{ETag="\"original\""});await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);
        await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();Assert.Equal("Foreign",Assert.Single(source.PendingTemporaryItems).Stage);Assert.True(fixture.Objects.ContainsKey(owned.Item.Path));Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "GET" or "DELETE");
    }
    /// <summary>弱标签、长度和同秒时间不能证明所有权，外部同长度内容保持原样。</summary>
    [Fact]
    public async Task RecordedWeakTagAndMatchingMetadataNeverDeleteReplacedContent()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);fixture.Put(owned.Item.Path,[99,98]);fixture.XmlTag=_=>"W/\"weak\"";
        owned.Repository.SaveTemporary(owned.Item with{ETag="W/\"weak\"",Size=2,ModifiedUtc=Modified});
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();
        Assert.Equal("CleanupPending",Assert.Single(source.PendingTemporaryItems).Stage);Assert.Equal(new byte[]{99,98},fixture.Objects[owned.Item.Path].Bytes);
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "GET" or "DELETE");
    }
    /// <summary>没有写回ETag时，创建后被外部替换的强版本必须核对摘要，不能直接取得清理权。</summary>
    [Fact]
    public async Task MissingWriteReceiptDoesNotClaimLaterStrongVersion()
    {
        await using var fixture=new DavFixture();var handle=fixture.Server.Handler;string? replaced=null;
        fixture.Server.Handler=async request=>
        {
            var response=await handle(request);
            if(replaced is null&&request.Method=="PUT"&&response.Status==201)
            {
                replaced=Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath[5..]);
                fixture.Put(replaced,[9,8]);
            }
            return response;
        };
        await using var source=fixture.Source();await source.UploadAsync("user.bin",new MemoryStream([1]),null);await source.DrainTemporaryCleanupAsync();
        Assert.NotNull(replaced);Assert.Equal(new byte[]{9,8},fixture.Objects[replaced].Bytes);
        Assert.Contains(source.PendingTemporaryItems,item=>item.Path==replaced&&item.Stage=="Foreign");
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE"&&r.Target.EndsWith(replaced,StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RecordedSizeOrTimeMismatchProtectsTemporaryResource(bool size)
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);owned.Repository.SaveTemporary(owned.Item with{Size=size?3:2,ModifiedUtc=size?Modified:Modified.AddSeconds(-1)});
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();Assert.Equal("Foreign",Assert.Single(source.PendingTemporaryItems).Stage);Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "GET" or "DELETE");
    }
    [Fact]
    public async Task MetadataConditionalDeleteRejectionKeepsForeignRecord()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);owned.Repository.SaveTemporary(owned.Item with{Size=2,ModifiedUtc=Modified});var handle=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="DELETE"?Task.FromResult(new HttpTestServer.Response(412,[])):handle(request);
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();Assert.Equal("Foreign",Assert.Single(source.PendingTemporaryItems).Stage);Assert.True(fixture.Objects.ContainsKey(owned.Item.Path));Assert.Contains("If-Match",Assert.Single(fixture.Server.Requests,r=>r.Method=="DELETE").Headers.Keys);
    }
    [Fact(Timeout=15000)]
    public async Task LegacyHashOutlivesFiveSecondsWithoutHoldingCallerDeadline()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var handle=fixture.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Handler=async request=>{var response=await handle(request);if(request.Method=="GET")return response with{BeforeBody=release.Task,HeadersSent=entered};return response;};
        await using var source=fixture.Source();source.ReadIdleTimeout=TimeSpan.FromSeconds(10);source.AttachTemporaryJournal(owned.Mount,owned.Repository);using var caller=new CancellationTokenSource(TimeSpan.FromSeconds(2));var watch=Stopwatch.StartNew();
        try
        {
            await source.RecoverTemporaryItemsAsync(caller.Token);Assert.True(watch.Elapsed<TimeSpan.FromSeconds(1.5));await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromSeconds(5.2));Assert.True(caller.IsCancellationRequested);Assert.True(fixture.Objects.ContainsKey(owned.Item.Path));release.TrySetResult();await source.DrainTemporaryCleanupAsync();
            Assert.True(watch.Elapsed>TimeSpan.FromSeconds(5));Assert.Empty(source.PendingTemporaryItems);Assert.False(fixture.Objects.ContainsKey(owned.Item.Path));
        }
        finally{release.TrySetResult();}
    }
    [Fact]
    public async Task FailedHashPersistsAttemptAndNextRunCanRecover()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var handle=fixture.Server.Handler;fixture.Server.Handler=request=>request.Method=="GET"?Task.FromResult(new HttpTestServer.Response(503,[])):handle(request);
        await using(var source=fixture.Source())
        {
            source.TemporaryRetryDelay=TimeSpan.Zero;source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();var pending=Assert.Single(source.PendingTemporaryItems);Assert.Equal(1,pending.HashAttempts);Assert.Equal("CleanupPending",pending.Stage);Assert.NotNull(pending.NextHashAttemptUtc);
        }
        Assert.Equal(1,Assert.Single(owned.Repository.TemporaryItems(owned.Mount.Id,owned.Item.Scope)).HashAttempts);fixture.Server.Handler=handle;
        await using var recovered=fixture.Source();recovered.AttachTemporaryJournal(owned.Mount,owned.Repository);await recovered.RecoverTemporaryItemsAsync();await recovered.DrainTemporaryCleanupAsync();Assert.Empty(recovered.PendingTemporaryItems);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DeferredHashesRespectPersistedBackoffAndAttemptCap(bool capped)
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);owned.Repository.SaveTemporary(owned.Item with{HashAttempts=capped?5:1,NextHashAttemptUtc=capped?null:DateTimeOffset.UtcNow.AddMinutes(1)});
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();Assert.Single(source.PendingTemporaryItems);Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "GET" or "DELETE");
    }
    [Fact]
    public async Task LegacyHashMismatchRemainsForeign()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);fixture.Put(owned.Item.Path,[9]);await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();Assert.Equal("Foreign",Assert.Single(source.PendingTemporaryItems).Stage);Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE");
    }
    [Fact]
    public async Task EmptyLegacyContentStillCleans()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture,[]);await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();Assert.Empty(source.PendingTemporaryItems);
    }
    [Fact]
    public async Task NewlyCreatedTemporaryMetadataIsPersistedBeforeFailedCleanup()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var handle=fixture.Server.Handler;fixture.Server.Handler=request=>request.Method=="DELETE"?Task.FromResult(new HttpTestServer.Response(409,[])):handle(request);
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.UploadAsync("user.bin",new MemoryStream([1]),null);await source.DrainTemporaryCleanupAsync();
        var created=source.PendingTemporaryItems.Where(item=>item.Path!=owned.Item.Path).ToArray();Assert.NotEmpty(created);Assert.All(created,item=>{Assert.NotNull(item.ETag);Assert.NotNull(item.Size);Assert.NotNull(item.ModifiedUtc);Assert.Equal(1,item.HashAttempts);});
        Assert.Contains(owned.Repository.TemporaryItems(owned.Mount.Id,owned.Item.Scope),item=>item.Path!=owned.Item.Path&&item.Size is not null&&item.ModifiedUtc is not null);
    }
    [Fact]
    public async Task LegacyBodyInactivityLeavesJournalForAnotherRun()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var handle=fixture.Server.Handler;var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Handler=async request=>{var response=await handle(request);return request.Method=="GET"?response with{BeforeBody=release.Task}:response;};
        await using var source=fixture.Source();source.ReadIdleTimeout=TimeSpan.FromMilliseconds(100);source.AttachTemporaryJournal(owned.Mount,owned.Repository);
        try
        {
            await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync().WaitAsync(TimeSpan.FromSeconds(3));var item=Assert.Single(source.PendingTemporaryItems);
            Assert.Equal("CleanupPending",item.Stage);Assert.Equal(1,item.HashAttempts);Assert.True(fixture.Objects.ContainsKey(item.Path));Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE");
        }
        finally{release.TrySetResult();}
    }
    [Fact]
    public async Task DisposalCancelsAndDrainsBackgroundHashWithoutLosingJournal()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var handle=fixture.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Handler=async request=>{var response=await handle(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=entered}:response;};
        var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);
        try
        {
            await source.RecoverTemporaryItemsAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var item=Assert.Single(owned.Repository.TemporaryItems(owned.Mount.Id,owned.Item.Scope));Assert.Equal("CleanupPending",item.Stage);Assert.Equal(1,item.HashAttempts);Assert.True(fixture.Objects.ContainsKey(item.Path));
        }
        finally{release.TrySetResult();}
    }
}
