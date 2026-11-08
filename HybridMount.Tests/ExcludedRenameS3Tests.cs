using HybridMount.Models.Mounting;
using HybridMount.Services;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>真实AWS SDK回环验证条件复制、版本保留及只继续删源；不连接正式桶。</summary>
public sealed class ExcludedRenameS3Tests(Xunit.Abstractions.ITestOutputHelper output)
{
    private async Task<MountOperationSnapshot> Wait(Area test,string state="Completed")
    {
        var end=DateTime.UtcNow.AddSeconds(30);MountOperationSnapshot? row=null;
        while(DateTime.UtcNow<end){row=test.Manager.Operations.FirstOrDefault(x=>x.Operation=="excludedRename");if(row?.State==state)return row;if(state=="Completed"&&row?.State is "Conflict" or "Failed")throw new IOException(row.IssueDetails);await Task.Delay(40);}
        throw new TimeoutException(System.Text.Json.JsonSerializer.Serialize(new{row,uploads=test.Repository.Uploads(test.Id)}));
    }
    [Theory(Timeout=90000)] [InlineData(false)] [InlineData(true)]
    public async Task MovePreservesHistoricalVersionAndNeverUploadsLocalEdits(bool copyFallback)
    {
        await using var test=await Area.Create();output.WriteLine("OwnedRoot="+System.Text.Json.JsonSerializer.Serialize(test.Repository.LoadDefinitions().Single()));test.Source.IgnoreCopyConditions=copyFallback;
        var original=test.Source.Objects["remote/123.txt"];await File.ReadAllBytesAsync(test.Old);await File.WriteAllTextAsync(test.Old,"unsent local change");await test.Manager.ScanUploadChangesAsync(test.Id);
        var identity=LocalFileState.Read(test.Old).Identity;File.Move(test.Old,test.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test);
        Assert.False(test.Source.Objects.ContainsKey("remote/123.txt"));Assert.Equal(original.Bytes,test.Source.Objects["remote/123.bak"].Bytes);Assert.True(test.Source.Versions.ContainsKey(("remote/123.txt",original.VersionId)));Assert.Equal("unsent local change",await File.ReadAllTextAsync(test.New));Assert.Equal(identity,LocalFileState.Read(test.New).Identity);Assert.Null(CloudFilesNative.Inspect(test.New));
        var put=Assert.Single(test.Source.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("/remote/123.bak",StringComparison.Ordinal));Assert.Equal("*",put.Headers["If-None-Match"]);
        if(copyFallback)Assert.Equal(original.Bytes,put.Body);else Assert.True(put.Headers.ContainsKey("x-amz-copy-source"));
        await File.WriteAllTextAsync(test.New,"later excluded edit");await test.Manager.ScanUploadChangesAsync(test.Id);await Task.Delay(250);Assert.Single(test.Source.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("/remote/123.bak",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task PartialMovePersistsReceiptAndExplicitRetryOnlyRemovesSource()
    {
        await using var test=await Area.Create();output.WriteLine("OwnedRoot="+System.Text.Json.JsonSerializer.Serialize(test.Repository.LoadDefinitions().Single()));test.Source.DeleteFailKey="remote/123.txt";
        File.Move(test.Old,test.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var partial=await Wait(test,"OutcomeUnknown");
        var end=DateTime.UtcNow.AddSeconds(5);while(DateTime.UtcNow<end&&test.Manager.GetOperation(partial.Id)?.Results.Single().Message.Contains("可显式重试",StringComparison.Ordinal)!=true)await Task.Delay(30);
        Assert.True(test.Source.Objects.ContainsKey("remote/123.txt"));Assert.True(test.Source.Objects.ContainsKey("remote/123.bak"));var puts=test.Source.Server.Requests.Count(x=>x.Method=="PUT"&&x.Target.EndsWith("/remote/123.bak",StringComparison.Ordinal));var deletes=test.Source.Server.Requests.Count(x=>x.Method=="DELETE"&&x.Target.EndsWith("/remote/123.txt",StringComparison.Ordinal));
        var ready=DateTime.UtcNow.AddSeconds(10);while(!test.Manager.RecheckExcludedRename(partial.Id)){if(DateTime.UtcNow>=ready)throw new TimeoutException("只读核对仍忙");await Task.Delay(20);}await Task.Delay(200);Assert.Equal(deletes,test.Source.Server.Requests.Count(x=>x.Method=="DELETE"&&x.Target.EndsWith("/remote/123.txt",StringComparison.Ordinal)));
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await Wait(test,"OutcomeUnknown");test.Source.DeleteFailKey=null;
        ready=DateTime.UtcNow.AddSeconds(10);while(!test.Manager.RetryUpload(partial.Id)){if(DateTime.UtcNow>=ready)throw new TimeoutException("等待显式重试入口");await Task.Delay(20);}await Wait(test);Assert.False(test.Source.Objects.ContainsKey("remote/123.txt"));Assert.Equal(puts,test.Source.Server.Requests.Count(x=>x.Method=="PUT"&&x.Target.EndsWith("/remote/123.bak",StringComparison.Ordinal)));Assert.Equal(test.Source.Objects["remote/123.bak"].Bytes,await File.ReadAllBytesAsync(test.New));
    }
    [Fact(Timeout=90000)]
    public async Task LostSourceDeleteResponseDoesNotReplayAndUsesSavedTargetReceipt()
    {
        await using var test=await Area.Create();var handler=test.Source.Server.Handler;var lost=false;
        test.Source.Server.Handler=async r=>{var result=await handler(r);if(!lost&&r.Method=="DELETE"&&r.Target.EndsWith("/remote/123.txt",StringComparison.Ordinal)){lost=true;throw new IOException("lost delete response");}return result;};
        File.Move(test.Old,test.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test);
        foreach(var r in test.Source.Server.Requests.Where(x=>x.Method=="DELETE"&&x.Target.EndsWith("/remote/123.txt",StringComparison.Ordinal)))output.WriteLine(System.Text.Json.JsonSerializer.Serialize(r.Headers.Where(x=>x.Key!="Authorization")));
        foreach(var shape in test.Wire)output.WriteLine(shape);
        Assert.True(lost);Assert.Single(test.Source.Server.Requests,x=>x.Method=="DELETE"&&x.Target.EndsWith("/remote/123.txt",StringComparison.Ordinal));Assert.Null(CloudFilesNative.Inspect(test.New));
    }
    private sealed class Factory(Area test):IFileSourceFactory
    {
        public async Task<HybridMount.Common.IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)
        {
            var source=new S3FileSource(mount.Source!,S3Fixture.Credentials,Path.Combine(repository.DataDirectory,"s3-work")){HttpAttemptForTests=value=>test.Wire.Enqueue(value)};
            ((IMountSourceRuntime)source).AttachRuntime(mount,repository);await source.ProbeAsync(token);return source;
        }
    }
    /// <summary>隔离源、注册配置和NTFS根；清理只注销私有本地根，不删除云端测试对象之外的数据。</summary>
    private sealed class Area:IAsyncDisposable
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Wire=new();internal S3Fixture Source=new();internal MountRepository Repository=null!;internal LoggerService Logger=null!;internal MountManager Manager=null!;internal Guid Id;private string Registry="";
        internal string Old=>Path.Combine(Source.Area,"view","123.txt");internal string New=>Path.Combine(Source.Area,"view","123.bak");
        internal static async Task<Area> Create()
        {
            var test=new Area();test.Source.Put("remote/123.txt",[1,3,5,7]);var local=Path.Combine(test.Source.Area,"view");Directory.CreateDirectory(local);test.Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
            test.Repository=new(Path.Combine(test.Source.Area,"data"),test.Registry);test.Logger=new(Path.Combine(test.Source.Area,"logs.db"));test.Manager=new(test.Repository,test.Logger,new Factory(test));
            var mount=await test.Manager.AddAsync("S3排除改名",local,false,source:test.Source.Options,credentials:S3Fixture.Credentials);test.Id=mount.Id;await test.Manager.SetSchedulingForTestsAsync(test.Id,false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);return test;
        }
        public async ValueTask DisposeAsync()
        {
            try{if(!Manager.IsStopping&&Manager.Mounts.Any(x=>x.Definition.Id==Id))await WebDavTests.Finished(Manager,Manager.Queue(Id,"clearAndRemove"));}finally{await Manager.DisposeAsync();await Logger.DisposeAsync();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Registry,false);await Source.DisposeAsync();}
        }
    }
}
