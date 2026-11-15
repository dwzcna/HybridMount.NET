using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Files;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols.S3;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Tests.Fixtures.Protocols;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
namespace HybridMount.Tests.Integration.Protocols;

/// <summary>真实 AWS SDK HTTP 请求验证，不使用接口模拟代替签名/协议/分页证据。</summary>
public sealed class S3Tests
{
    [Fact]
    public async Task SigV4PathStylePaginationPrefixAndVersionPinnedRange()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();await source.ProbeAsync();
        var entries=await WebDavTests.All(source.ListAsync(""));Assert.Equal(3,entries.Count);Assert.Contains(entries,item=>item.Path=="empty"&&item.IsDirectory);
        Assert.Contains(fixture.Server.Requests,request=>request.Target.Contains("continuation-token="));
        var file=await source.GetMetadataAsync("文件.bin");var original=fixture.Objects["remote/文件.bin"].Bytes;
        fixture.Put("remote/文件.bin",[1,2,3]);var bytes=new byte[73];Assert.Equal(73,await source.ReadRangeAsync(file,4093,bytes));Assert.Equal(original[4093..4166],bytes);
        Assert.All(fixture.Server.Requests,request=>Assert.StartsWith("/test-bucket",request.Target));
        Assert.All(fixture.Server.Requests,S3Fixture.VerifySignature);
        Assert.Equal(CapabilitySupport.Supported,source.Capabilities.RangeRead);Assert.Equal(CapabilitySupport.Unsupported,source.Capabilities.StableIds);
        Assert.All(entries,entry=>Assert.Equal("",entry.StableId));
    }
    [Fact]
    public async Task RejectRepeatedPaginationCollisionAndBadRange()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        fixture.RepeatToken=true;await Assert.ThrowsAsync<IOException>(()=>WebDavTests.All(source.ListAsync("")));fixture.RepeatToken=false;
        fixture.Put("remote/conflict",[1]);fixture.Put("remote/conflict/child",[2]);var diagnostics=new List<SourceReadDiagnostic>();source.ReadDiagnostic+=diagnostics.Add;
        var entries=await WebDavTests.All(source.ListAsync(""));Assert.Single(entries,item=>item.Path=="conflict");Assert.Contains(entries,item=>item.Path=="文件.bin");Assert.Contains(diagnostics,item=>item.Strategy=="list"&&item.FallbackReason=="item_collision");
        fixture.Objects.TryRemove("remote/conflict",out _);fixture.Objects.TryRemove("remote/conflict/child",out _);
        var file=await source.GetMetadataAsync("文件.bin");fixture.BadRange=true;
        await Assert.ThrowsAsync<SourceConflictException>(async()=>await source.ReadRangeAsync(file,4,new byte[3]));
    }
    [Fact]
    public async Task ConditionalWritesMarkersCopyMoveAndDeleteKeepVersions()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        var file=await source.UploadAsync("new.bin",new MemoryStream([1,2,3]),null);
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("new.bin",Stream.Null,null));
        var updated=await source.UploadAsync("new.bin",new MemoryStream([4,5]),file.Version);
        await source.CreateDirectoryAsync("target");var moved=await source.MoveAsync("new.bin","target/moved.bin",updated.Version);
        Assert.Equal(2,moved.Size);Assert.False(fixture.Objects.ContainsKey("remote/new.bin"));
        var versions=fixture.Versions.Count;await source.DeleteAsync(moved.Path,moved.Version);Assert.Equal(versions,fixture.Versions.Count);
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&request.Target.Contains("versionId="));
        Assert.DoesNotContain(fixture.Objects.Keys,key=>key.Contains(".hybridmount-probe-"));
        Assert.Contains(fixture.Server.Requests,request=>request.Headers.ContainsKey("x-amz-copy-source")&&!request.Target.Contains("probe"));
    }
    [Fact]
    public async Task UnsupportedCopyConditionsUseSafeDownloadAndConditionalPut()
    {
        await using var fixture=new S3Fixture{IgnoreCopyConditions=true};await using var source=fixture.Source();
        var file=await source.GetMetadataAsync("文件.bin");await source.MoveAsync(file.Path,"moved.bin",file.Version);
        Assert.Contains(fixture.Server.Requests,request=>request.Method=="GET"&&request.Target.Contains("%E6%96%87%E4%BB%B6.bin",StringComparison.OrdinalIgnoreCase));
        Assert.Equal(file.Size,fixture.Objects["remote/moved.bin"].Bytes.Length);Assert.False(fixture.Objects.ContainsKey("remote/文件.bin"));
    }
    [Fact]
    public async Task MultipartUploadAndCancellationCleanOnlyOwnedUploads()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        var bytes=Enumerable.Range(0,9*1024*1024+17).Select(index=>(byte)(index*31)).ToArray();
        var entry=await source.UploadAsync("large.bin",new MemoryStream(bytes),null);Assert.Equal(bytes.Length,entry.Size);Assert.Equal(bytes,fixture.Objects["remote/large.bin"].Bytes);
        Assert.Empty(fixture.Uploads);Assert.True(fixture.Aborts>=2);Assert.Contains(fixture.Server.Requests,request=>request.Target.Contains("partNumber=2"));
        using var cancel=new CancellationTokenSource();fixture.OnPart=key=>{if(key=="remote/cancel.bin")cancel.Cancel();};
        var error=await Record.ExceptionAsync(()=>source.UploadAsync("cancel.bin",new MemoryStream(bytes),null,cancel.Token));
        Assert.True(error is OperationCanceledException or SourceOutcomeUnknownException,error?.ToString());Assert.Empty(fixture.Uploads);Assert.False(fixture.Objects.ContainsKey("remote/cancel.bin"));
    }
    [Fact]
    public async Task DirectoryMoveReportsPartialResultAndPreservesFailedSource()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/a.bin",[1]);fixture.Put("remote/tree/b.bin",[2]);fixture.DeleteFailKey="remote/tree/b.bin";
        await using var source=fixture.Source();var directory=await source.GetMetadataAsync("tree");
        var partial=await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync("tree","destination",directory.Version));
        Assert.Contains(partial.Results,item=>item.Path=="tree/a.bin"&&item.Status=="Completed");
        Assert.Contains(partial.Results,item=>item.Path=="tree/b.bin"&&item.Status=="Copied");
        Assert.False(fixture.Objects.ContainsKey("remote/tree/a.bin"));Assert.True(fixture.Objects.ContainsKey("remote/tree/b.bin"));Assert.True(fixture.Objects.ContainsKey("remote/destination/b.bin"));
    }
    [Fact]
    public async Task UncertainWriteIsNotRetriedAndDirectoryTargetsAreProtected()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("dir",new MemoryStream([99]),null));
        fixture.FailPutKey="remote/uncertain.bin";
        await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.UploadAsync("uncertain.bin",new MemoryStream([1]),null));
        Assert.Equal(1,fixture.Server.Requests.Count(request=>request.Method=="PUT"&&request.Target.EndsWith("uncertain.bin")));
        Assert.True(fixture.Objects.ContainsKey("remote/dir/child.bin"));
    }
    [Fact]
    public async Task IgnoredConditionsRejectUserWrite()
    {
        await using var fixture=new S3Fixture{IgnorePutConditions=true};await using var source=fixture.Source();
        await Assert.ThrowsAsync<NotSupportedException>(()=>source.UploadAsync("user.bin",Stream.Null,null));
        Assert.False(fixture.Objects.ContainsKey("remote/user.bin"));Assert.Equal(CapabilitySupport.Unsupported,source.Capabilities.ConditionalWrite);
    }
    [Fact]
    public async Task InterruptedMultipartRecoveryAbortsOnlyJournaledUploads()
    {
        await using var fixture=new S3Fixture();using var cancel=new CancellationTokenSource();
        await using(var source=fixture.Source())
        {
            fixture.AbortFailKey="remote/interrupted.bin";fixture.OnPart=key=>{if(key==fixture.AbortFailKey)cancel.Cancel();};
            var error=await Record.ExceptionAsync(()=>source.UploadAsync("interrupted.bin",new MemoryStream(new byte[6*1024*1024]),null,cancel.Token));Assert.NotNull(error);
            Assert.Single(fixture.Uploads);Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Area,"work"),"*.multipart.json"));
        }
        fixture.Uploads["foreign-upload"]=new("remote/foreign.bin",new());fixture.AbortFailKey=null;fixture.OnPart=null;
        await using(var resumed=fixture.Source())
        {
            await resumed.ProbeAsync();Assert.Equal(2,fixture.Uploads.Count);
            await resumed.RecoverKnownUploadsAsync();
        }
        Assert.Single(fixture.Uploads);Assert.True(fixture.Uploads.ContainsKey("foreign-upload"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Area,"work"),"*.multipart.json"));Assert.False(fixture.Objects.ContainsKey("remote/interrupted.bin"));
    }
    [Fact(Timeout=120000)]
    public async Task RealCfApiS3HydrationAndRemovalKeepRemoteObjects()
    {
        await using var fixture=new S3Fixture();var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);var count=fixture.Objects.Count;
        var mount=await manager.AddAsync("S3 CFAPI 测试",local,true,source:fixture.Options,credentials:S3Fixture.Credentials);
        try
        {
            var expected=fixture.Objects["remote/文件.bin"].Bytes;Assert.Equal(expected,await Task.Run(()=>File.ReadAllBytes(Path.Combine(local,"文件.bin"))).WaitAsync(TimeSpan.FromSeconds(20)));
            var release=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"freeSpace"));Assert.Equal("Completed",release.State);
            var removed=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"remove"));Assert.True(removed.State=="Completed",removed.Error);
            Assert.Equal(expected,await File.ReadAllBytesAsync(Path.Combine(local,"文件.bin")));Assert.Equal(count,fixture.Objects.Count);Assert.Null(MountPathGuard.ReadRoot(local));
        }
        finally
        {
            if(manager.Mounts.Count>0){var result=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Assert.True(result.State=="Completed",result.Error);}
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
}
