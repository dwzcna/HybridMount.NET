using HybridMount.Commons.Persistence;
using HybridMount.Commons.Protocols;
using HybridMount.Commons.Settings;
using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Tests.Fixtures.Protocols;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
namespace HybridMount.Tests.Integration.Protocols;

/// <summary>实际只读认证及持久接纳边界；目录、HTTP响应器、数据库和凭据均为隔离夹具。</summary>
public sealed class ConnectionSubmissionTests
{
    [Theory(Timeout=30000)]
    [InlineData(FileSourceKind.WebDav)][InlineData(FileSourceKind.S3)][InlineData(FileSourceKind.Synology)]
    public async Task AuthenticationFailureDoesNotSaveOrCreateTask(FileSourceKind kind)
    {
        await using var server=new HttpTestServer();server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(401,"denied"));
        await using var test=new Area();var source=new FileSourceConfiguration(kind,server.Address.AbsoluteUri,"bucket",kind==FileSourceKind.Synology?"/share":"",AllowHttp:true);
        var draft=test.Draft(source);await Assert.ThrowsAsync<ConnectionSubmissionException>(()=>test.Manager.AuthenticateSubmissionAsync(draft,null,default));
        Assert.Empty(test.Store.Items);Assert.Empty(test.Repository.LoadOperations());Assert.Empty(Directory.GetFiles(test.Repository.DataDirectory,"*.bin",SearchOption.AllDirectories));
        Assert.Equal(0,test.Factory.Calls);Assert.All(server.Requests,request=>Assert.DoesNotContain(request.Method,new[]{"PUT","DELETE","MKCOL","MOVE"}));
    }
    [Fact(Timeout=30000)]
    public async Task SlowAuthenticationAndCancellationKeepDraftAndHaveNoTask()
    {
        await using var dav=new DavFixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var original=dav.Server.Handler;
        dav.Server.Handler=async request=>{var response=await original(request);if(request.Method=="PROPFIND"){entered.TrySetResult();return response with{BeforeBody=release.Task};}return response;};
        await using var test=new Area();var draft=test.Draft(dav.Options);using var cancel=new CancellationTokenSource();
        var authentication=test.Manager.AuthenticateSubmissionAsync(draft,null,cancel.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(authentication.IsCompleted);Assert.Empty(test.Store.Items);Assert.Empty(test.Repository.LoadOperations());cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>authentication);release.TrySetResult();Assert.Equal("draft-secret",draft.Credentials.Password);Assert.Empty(test.Repository.LoadOperations());
    }
    [Theory(Timeout=30000)]
    [InlineData(false)][InlineData(true)]
    public async Task AcceptedTaskOutlivesFormAndDoesNotWaitForInitialization(bool failSave)
    {
        await using var dav=new DavFixture();await using var test=new Area();var draft=test.Draft(dav.Options);var from=DateTimeOffset.UtcNow;
        var proof=await test.Manager.AuthenticateSubmissionAsync(draft,null,default);var authenticated=DateTimeOffset.UtcNow;test.Store.FailSave=failSave;
        var accepted=await test.Manager.AcceptSubmissionAsync(proof,default).WaitAsync(TimeSpan.FromSeconds(2));await proof.DisposeAsync();
        if(failSave)
        {
            var failed=await WebDavTests.Finished(test.Manager,accepted.TaskId);Assert.Equal("Failed",failed.State);Assert.Empty(test.Store.Items);Assert.Equal("OperationStageSaveConfiguration",failed.FailureStage);
        }
        else
        {
            await test.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Single(test.Store.Items);
            var current=test.Manager.GetOperation(accepted.TaskId)!;Assert.Equal("connect",current.Operation);Assert.Equal("Running",current.State);Assert.Equal(accepted.MountId,current.MountId);
            Assert.Single(test.Repository.LoadOperations());var run=Assert.Single(test.Repository.OperationRuns(accepted.TaskId));
            var auth=run.Steps.Single(step=>step.Name=="OperationStageAuthenticate");Assert.Equal("Completed",auth.State);Assert.InRange(auth.StartedUtc!.Value,from,authenticated);Assert.InRange(auth.FinishedUtc!.Value,from,authenticated);
            Assert.Equal("Completed",run.Steps.Single(step=>step.Name=="OperationStageSaveConfiguration").State);Assert.Equal("Running",run.Steps.Single(step=>step.Name=="OperationStageConnectSource").State);
            Assert.True(test.Manager.Cancel(accepted.TaskId));var canceled=await WebDavTests.Finished(test.Manager,accepted.TaskId);Assert.Equal("Canceled",canceled.State);Assert.Single(test.Store.Items);
            Assert.False(test.Manager.Cancel(accepted.TaskId));Assert.Single(test.Repository.OperationRuns(accepted.TaskId));
        }
        Assert.DoesNotContain("draft-secret",System.Text.Json.JsonSerializer.Serialize(test.Repository.LoadOperations()));
    }
    [Fact(Timeout=30000)]
    public async Task AcceptanceFailureRollsBackAndProofCanBeSafelyDisposed()
    {
        await using var dav=new DavFixture();await using var test=new Area();await using var proof=await test.Manager.AuthenticateSubmissionAsync(test.Draft(dav.Options),null,default);
        using(var db=new SqliteConnection("Pooling=false;Data Source="+Path.Combine(test.Repository.DataDirectory,"mounts.db"))){db.Open();MountDatabase.ConfigurePathComparison(db);using var cmd=db.CreateCommand();cmd.CommandText="CREATE TRIGGER reject_accept BEFORE INSERT ON operation_steps BEGIN SELECT RAISE(ABORT,'isolated acceptance failure'); END";cmd.ExecuteNonQuery();}
        await Assert.ThrowsAsync<SqliteException>(()=>test.Manager.AcceptSubmissionAsync(proof,default));Assert.False(proof.Transferred);Assert.Empty(test.Repository.LoadOperations());Assert.Empty(test.Store.Items);Assert.Equal(0,test.Factory.Calls);
    }
    [Fact(Timeout=30000)]
    public async Task DuplicateSubmissionIsBoundedAndCanceledProofReleasesItsReservation()
    {
        await using var dav=new DavFixture();await using var test=new Area();var draft=test.Draft(dav.Options);
        var proof=await test.Manager.AuthenticateSubmissionAsync(draft,null,default);
        await Assert.ThrowsAsync<IOException>(async()=>await test.Manager.AuthenticateSubmissionAsync(draft,null,default));await proof.DisposeAsync();
        await using var second=await test.Manager.AuthenticateSubmissionAsync(draft,null,default);Assert.Empty(test.Store.Items);Assert.Empty(test.Repository.LoadOperations());
    }
    [Fact(Timeout=30000)]
    public async Task ExistingCredentialsAreUntouchedUntilFreshAuthenticationSucceeds()
    {
        await using var server=new HttpTestServer();server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(401,"denied"));await using var test=new Area();
        var source=new FileSourceConfiguration(FileSourceKind.WebDav,server.Address.AbsoluteUri,AllowHttp:true);
        var definition=new MountDefinition(Guid.NewGuid(),"existing",test.Local,false,Source:source,RegistrationStage:MountRegistrationStage.NotRegistered);test.Store.SaveDefinition(definition);
        await using var manager=new MountManager(test.Repository,test.Logger,test.Factory);new CredentialStore(test.Repository.DataDirectory).Save(definition.Id,new("original","original-secret"));
        var draft=test.Draft(source) with{ExistingId=definition.Id};await Assert.ThrowsAsync<ConnectionSubmissionException>(()=>manager.AuthenticateSubmissionAsync(draft,null,default));
        Assert.Equal("original-secret",new CredentialStore(test.Repository.DataDirectory).Load(definition.Id).Password);Assert.Equal(definition,Assert.Single(test.Store.Items));Assert.Empty(test.Repository.LoadOperations());
    }
    [Fact(Timeout=30000)]
    public async Task S3FreshAuthenticationUsesSignedReadOnlyRequests()
    {
        await using var s3=new S3Fixture();await using var test=new Area();
        var draft=test.Draft(s3.Options) with{Credentials=S3Fixture.Credentials};
        await using var proof=await test.Manager.AuthenticateSubmissionAsync(draft,null,default);
        Assert.Empty(test.Store.Items);Assert.Empty(test.Repository.LoadOperations());Assert.NotEmpty(s3.Server.Requests);
        Assert.All(s3.Server.Requests,request=>{Assert.Equal("GET",request.Method);S3Fixture.VerifySignature(request);});
    }
    [Fact(Timeout=30000)]
    public async Task SynologyFreshOtpProofIsOwnedAndDisposingUnacceptedProofLogsOut()
    {
        using var dsm=new HybridMount.Tests.Fixtures.Protocols.SynologyFakeDsm{AuthMax=3,RequireOtp=true};using var bridge=new HttpClient(dsm);await using var server=new HttpTestServer();
        server.Handler=async incoming=>
        {
            using var request=new HttpRequestMessage(new HttpMethod(incoming.Method),new Uri(server.Address,incoming.Target));
            if(incoming.Body.Length>0){request.Content=new ByteArrayContent(incoming.Body);if(incoming.Headers.TryGetValue("Content-Type",out var type))request.Content.Headers.TryAddWithoutValidation("Content-Type",type);}
            using var response=await bridge.SendAsync(request);return new((int)response.StatusCode,await response.Content.ReadAsByteArrayAsync());
        };
        await using var test=new Area();var options=new FileSourceConfiguration(FileSourceKind.Synology,server.Address.AbsoluteUri,Prefix:"/share",AllowHttp:true);var draft=test.Draft(options);
        var proof=await test.Manager.AuthenticateSubmissionAsync(draft,"123456",default);Assert.NotNull(proof.Session);Assert.Single(dsm.Logins);Assert.Equal("123456",dsm.Logins.Single()["otp_code"]);
        Assert.Empty(test.Store.Items);await proof.DisposeAsync();Assert.Contains(dsm.Requests,item=>item.GetValueOrDefault("method")=="logout");
        await Assert.ThrowsAsync<ConnectionSubmissionException>(()=>test.Manager.AuthenticateSubmissionAsync(draft,null,default));Assert.Empty(test.Repository.LoadOperations());
    }
    [Fact]
    public void FrozenDraftDoesNotBorrowMutableRulesAndDiagnosticsHaveNoCredentials()
    {
        var rules=FileExclusionRules.Defaults().ToList();var draft=new ConnectionSubmissionDraft(null,"name","D:\\isolated",false,new(FileSourceKind.WebDav,"https://example.test/"),new("account","never-log-this"),rules,new());var frozen=draft.Freeze();rules.Clear();
        Assert.NotEmpty(frozen.Exclusions);Assert.False(frozen.SameInput(draft));Assert.DoesNotContain("never-log-this",frozen.ToString());
    }
    private sealed class Store:IMountDefinitionStore
    {
        internal readonly List<MountDefinition> Items=[];internal bool FailSave;
        public long CacheLimitBytes{get;set;}public IReadOnlyList<MountDefinition> LoadDefinitions()=>Items.ToArray();
        public void SaveDefinition(MountDefinition item){if(FailSave)throw new IOException("isolated save failure");Items.RemoveAll(value=>value.Id==item.Id);Items.Add(item);}
        public void DeleteDefinition(Guid id)=>Items.RemoveAll(item=>item.Id==id);
    }
    private sealed class Factory:IFileSourceFactory
    {
        internal int Calls;internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IFileSource> CreateAsync(MountDefinition mount,IMountRepository repository,CancellationToken token){Interlocked.Increment(ref Calls);Entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);throw new IOException("unreachable");}
    }
    private sealed class Area:IAsyncDisposable
    {
        internal readonly string PathRoot=Path.Combine(Path.GetTempPath(),"HybridMount.SubmissionTests",Guid.NewGuid().ToString("N"));
        internal string Local{get;}internal Store Store{get;}=new();internal Factory Factory{get;}=new();internal MountRepository Repository{get;}internal LoggerService Logger{get;}internal MountManager Manager{get;}
        internal Area(){Local=Path.Combine(PathRoot,"local");Directory.CreateDirectory(Local);Repository=new(Path.Combine(PathRoot,"data"),definitions:Store);Logger=new(Path.Combine(PathRoot,"logs.db"));Manager=new(Repository,Logger,Factory);}
        internal ConnectionSubmissionDraft Draft(FileSourceConfiguration source)=>new(null,"draft",Local,false,source,new("user","draft-secret",AccessKey:"access",SecretKey:"secret"),FileExclusionRules.Defaults(),new(RemoteAutoDetectEnabled:false));
        public async ValueTask DisposeAsync(){await Manager.DisposeAsync();await Logger.DisposeAsync();if(Directory.Exists(PathRoot))Directory.Delete(PathRoot,true);}
    }
}
