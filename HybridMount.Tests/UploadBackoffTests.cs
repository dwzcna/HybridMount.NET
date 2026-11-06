using System.Reflection;
using System.Text.Json;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class UploadBackoffTests
{
    private static LocalUpload Item(DateTimeOffset now)=>new("upload",Guid.NewGuid(),"file","file",false,"stamp",1,"Waiting",now.AddSeconds(-10));
    [Theory]
    [InlineData(0,true)][InlineData(408,true)][InlineData(425,true)][InlineData(429,true)]
    [InlineData(500,true)][InlineData(502,true)][InlineData(503,true)][InlineData(504,true)]
    [InlineData(400,false)][InlineData(401,false)][InlineData(403,false)][InlineData(404,false)][InlineData(409,false)][InlineData(501,false)][InlineData(505,false)]
    public void HttpClassification(int status,bool transient)
    {
        Assert.Equal(transient,UploadRules.IsTransient(new SourceRequestRejectedException(status,"rejected")));
        Assert.Equal(transient,UploadRules.IsTransient(new SourceConnectionException(new("upload",ConnectionFailureKind.Service,status,null,null,null,"service"))));
    }
    [Theory]
    [InlineData("network",true)][InlineData("timeout",true)][InlineData("canceled",true)][InlineData("http",true)]
    [InlineData("socket",true)][InlineData("diagnostic",true)][InlineData("unknown",true)][InlineData("io",false)][InlineData("permission",false)]
    public void ExceptionClassification(string kind,bool transient)
    {
        Exception error=kind switch
        {
            "network"=>new SourceConnectionException(new("upload",ConnectionFailureKind.Network,null,null,null,null,"network")),
            "timeout"=>new TimeoutException(),"canceled"=>new TaskCanceledException(),"http"=>new System.Net.Http.HttpRequestException(),
            "socket"=>new System.Net.Sockets.SocketException(),"diagnostic"=>new SourceConnectionException(new("upload",ConnectionFailureKind.Service,403,null,null,null,"transient",Transient:true)),
            "unknown"=>new SourceOutcomeUnknownException("probe",new IOException()),"permission"=>new UnauthorizedAccessException(),_=>new IOException()
        };Assert.Equal(transient,UploadRules.IsTransient(error));
    }
    /// <summary>协议的结果不明包装不能使发送前的证书或权限故障变为无限瞬时重试。</summary>
    [Theory]
    [InlineData(ConnectionFailureKind.Certificate)][InlineData(ConnectionFailureKind.Tls)]
    [InlineData(ConnectionFailureKind.Authentication)][InlineData(ConnectionFailureKind.Permission)]
    public void WrappedPermanentConnectionFailureRetainsClassification(ConnectionFailureKind kind)
    {
        var diagnostic=new SourceConnectionException(new("probe",kind,null,null,null,null,"永久诊断"));
        var error=new SourceOutcomeUnknownException("probe",new IOException("包装",diagnostic));
        Assert.True(UploadRules.IsPermanentConnectionFailure(error));Assert.False(UploadRules.IsTransient(error));
    }
    /// <summary>相同永久诊断在发送前失败，已发送时保留未知结果，不丢弃远端核对证据。</summary>
    [Theory]
    [InlineData("Prepared",ConnectionFailureKind.Certificate)][InlineData("Prepared",ConnectionFailureKind.Permission)]
    [InlineData("Sending",ConnectionFailureKind.Certificate)][InlineData("Sending",ConnectionFailureKind.Permission)]
    public async Task WrappedPermanentFailuresRespectPersistedSendPhase(string state,ConnectionFailureKind kind)
    {
        await using var dav=new DavFixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(dav.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(dav.Area,"logs.db"));await using var manager=new MountManager(repository,logger);
            var item=Item(DateTimeOffset.UtcNow) with{State=state,TransientFailureCount=2};repository.SaveUpload(item);
            var error=new SourceOutcomeUnknownException("probe",new IOException("包装",new SourceConnectionException(new("probe",kind,null,null,null,null,"永久诊断"))));
            typeof(MountManager).GetMethod("HandleUploadFailure",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(manager,[item.Id,error,true,CancellationToken.None,null]);
            var result=repository.Upload(item.Id)!;Assert.Equal(state=="Sending"?"OutcomeUnknown":"Failed",result.State);Assert.Null(result.NextAttemptUtc);Assert.Equal(2,result.TransientFailureCount);
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    [Fact]
    public void BackoffGrowsWithJitterAndRespectsLongerRetryAfter()
    {
        for(var failure=0;failure<8;failure++)
        {
            var expected=Math.Min(300,3*Math.Pow(2,failure));Assert.Equal(expected,UploadRules.TransientDelay(failure,jitter:0.5).TotalSeconds);
            Assert.InRange(UploadRules.TransientDelay(failure,jitter:0).TotalSeconds,Math.Min(300,expected*0.8)-0.000001,expected);
            Assert.InRange(UploadRules.TransientDelay(failure,jitter:1).TotalSeconds,expected,Math.Min(300,expected*1.2)+0.000001);
        }
        Assert.Equal(TimeSpan.FromSeconds(120),UploadRules.TransientDelay(0,TimeSpan.FromSeconds(120),0.5));
        Assert.Equal(TimeSpan.FromSeconds(192),UploadRules.TransientDelay(6,TimeSpan.FromSeconds(120),0.5));
        Assert.Equal(TimeSpan.FromHours(1),UploadRules.TransientDelay(0,TimeSpan.FromHours(1),0.5));
        Assert.Equal(TimeSpan.FromMinutes(5),UploadRules.TransientDelay(int.MaxValue,jitter:0));
    }
    [Fact]
    public void SchedulerSelectsDueItemsAndWaitsUntilEarliestDueOrScan()
    {
        var now=DateTimeOffset.UtcNow;var future=Item(now) with{NextAttemptUtc=now.AddSeconds(30),IsDirectory=true};var due=Item(now) with{Id="due",NextAttemptUtc=now};
        Assert.Same(due,UploadRules.NextUpload([future,due],now));Assert.Null(UploadRules.NextUpload([future],now));Assert.Same(future,UploadRules.NextUpload([future],now.AddSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(30),UploadRules.UploadWait([future],now,now.AddMinutes(1)));
        Assert.Equal(TimeSpan.FromSeconds(10),UploadRules.UploadWait([future],now,now.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromMinutes(1),UploadRules.UploadWait([],now,now.AddMinutes(1)));Assert.Null(UploadRules.NextUpload([],now));
        var legacy=Item(now) with{LastCheckedUtc=now};Assert.Null(UploadRules.NextUpload([legacy],now));Assert.Null(UploadRules.NextUpload([legacy],now.AddSeconds(3).AddTicks(-1)));
        Assert.Same(legacy,UploadRules.NextUpload([legacy],now.AddSeconds(3)));Assert.Equal(TimeSpan.FromSeconds(3),UploadRules.UploadWait([legacy],now,now.AddMinutes(1)));
        var uncheckedItem=Item(now);Assert.Same(uncheckedItem,UploadRules.NextUpload([uncheckedItem],now));
    }
    [Theory]
    [InlineData("Sending",503,false,"Waiting")][InlineData("Sending",425,false,"Waiting")][InlineData("Sending",403,false,"Failed")]
    [InlineData("Prepared",503,false,"Waiting")][InlineData("Prepared",-1,false,"Failed")][InlineData("Prepared",-1,true,"Waiting")]
    [InlineData("Sending",-1,false,"OutcomeUnknown")]
    public async Task FailureHandlingPersistsOnlySafeTransientRetries(string state,int status,bool preparing,string expected)
    {
        await using var dav=new DavFixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(dav.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(dav.Area,"logs.db"));await using var manager=new MountManager(repository,logger);
            var item=Item(DateTimeOffset.UtcNow) with{State=state,TransientFailureCount=2};repository.SaveUpload(item);
            Exception error=status<0?new IOException("永久读写错误"):new SourceRequestRejectedException(status,"服务拒绝",TimeSpan.FromSeconds(60));
            typeof(MountManager).GetMethod("HandleUploadFailure",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(manager,[item.Id,error,preparing,CancellationToken.None,null]);
            var result=new MountRepository(repository.DataDirectory,registry).Upload(item.Id)!;Assert.Equal(expected,result.State);
            if(status>=0&&expected=="Waiting")
            {Assert.Equal(3,result.TransientFailureCount);Assert.Equal(TimeSpan.FromSeconds(60),result.NextAttemptUtc-result.LastCheckedUtc);Assert.Contains("下次重试",result.Error);}
            else Assert.Equal(2,result.TransientFailureCount);
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    [Fact]
    public async Task ExplicitRetryBypassesDelayAndCommitResetsFailures()
    {
        await using var dav=new DavFixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(dav.Area,"data"),registry);var item=Item(DateTimeOffset.UtcNow) with{NextAttemptUtc=DateTimeOffset.UtcNow.AddMinutes(5),TransientFailureCount=4};
            repository.SaveDefinition(new(item.MountId,"重试",dav.Area,false,Source:dav.Options,RegistrationStage:MountRegistrationStage.NotRegistered));repository.SaveUpload(item);
            await using var logger=new LoggerService(Path.Combine(dav.Area,"logs.db"));await using var manager=new MountManager(repository,logger);
            Assert.True(manager.RetryUpload(item.Id));var retried=repository.Upload(item.Id)!;Assert.Null(retried.NextAttemptUtc);Assert.Same(retried,UploadRules.NextUpload([retried],DateTimeOffset.UtcNow));
            var receipt=new SourceEntry("","file",false,0,DateTimeOffset.UtcNow,"v1");var entry=new MountEntry(item.Id,"file","file","v1",false);
            var completed=repository.CommitUploadedEntry(retried,entry,receipt,null,missing:true);Assert.Equal(0,completed.TransientFailureCount);Assert.Null(completed.NextAttemptUtc);
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    [Fact]
    public void OldJsonDefaultsAndWaitingMessageRoundTrip()
    {
        var now=DateTimeOffset.UtcNow;var item=Item(now);var old=JsonSerializer.SerializeToNode(item)!;old.AsObject().Remove("NextAttemptUtc");old.AsObject().Remove("TransientFailureCount");
        var restored=old.Deserialize<LocalUpload>()!;Assert.Null(restored.NextAttemptUtc);Assert.Equal(0,restored.TransientFailureCount);
        var waiting=UploadRules.WaitForTransient(restored,new TimeoutException("连接超时"),now);var saved=JsonSerializer.Deserialize<LocalUpload>(JsonSerializer.Serialize(waiting))!;
        Assert.Equal(waiting,saved);Assert.Equal(1,saved.TransientFailureCount);Assert.Contains("下次重试",saved.Error);Assert.Null(UploadRules.NextUpload([saved],now));
    }
}
