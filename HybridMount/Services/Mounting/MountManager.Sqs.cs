using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private SqsEventHub QueueEvents=>(SqsEventHub)_remoteEvents;
    private IAsyncDisposable CreateRemoteEvents()=>new SqsEventHub(_repository,CreateQueueClient,TasksQueue);
    /// <summary>实例级队列传输注入，仅测试程序集可设置，不经持久配置或 IPC 暴露。</summary>
    internal Func<FileSourceConfiguration,SqsConfiguration,SourceCredentials,ISqsQueueClient>? SqsClientForTests {get;set;}
    private ISqsQueueClient CreateQueueClient(FileSourceConfiguration source,SqsConfiguration options,SourceCredentials keys)=>SqsClientForTests?.Invoke(source,options,keys)??new SqsQueueClient(source,options,keys);
    private CredentialStore SqsStore()=>new(Path.Combine(_repository.DataDirectory,"Sqs"));
    /// <summary>仅供本进程设置界面读取当前用户 DPAPI；不经日志或 IPC 输出。</summary>
    public SourceCredentials GetSqsCredentials(Guid id){Root(id);return SqsStore().Load(id);}
    private SourceCredentials QueueCredentials(MountDefinition mount,SqsConfiguration options,SourceCredentials? current=null)=>options.CredentialMode switch
    {SqsCredentialMode.ReuseS3=>mount.Source?.UseDefaultCredentials==true?new():new CredentialStore(_repository.DataDirectory).Load(mount.Id),SqsCredentialMode.SeparateKeys=>current??SqsStore().Load(mount.Id),_=>new()};
    private static void ValidateSqsSettings(FileSourceConfiguration source,SqsConfiguration? options,SourceCredentials? keys=null)
    {
        if(options is null)return;
        if(!Enum.IsDefined(options.CredentialMode)||options.QueueUrl is null||options.Region is null||options.QueueUrl.Length>2048||options.Region.Length>64)throw new ArgumentException("SQS 配置无效。");
        if(options.QueueUrl.Length>0&&(!Uri.TryCreate(options.QueueUrl,UriKind.Absolute,out var uri)||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0))throw new ArgumentException("队列 URL 不能包含凭据、查询或片段。");
        if(options.Enabled)SqsQueueClient.Validate(source,options);
        if(options.CredentialMode==SqsCredentialMode.SeparateKeys&&options.Enabled&&(keys is null||string.IsNullOrWhiteSpace(keys.AccessKey)||string.IsNullOrWhiteSpace(keys.SecretKey)))throw new ArgumentException("请填写独立 SQS 访问密钥。");
    }
    /// <summary>表单当前选项只读测试：不接收/确认消息，不创建队列，不修改桶通知；仍由应用退出所有者取消。</summary>
    public Task<ConnectionTestResult> TestSqsConnectionAsync(Guid id,SqsConfiguration options,SourceCredentials? credentials=null,CancellationToken token=default)
        =>OwnAsync(async cancel=>
        {
            var root=Root(id);MountDefinition mount;SourceCredentials keys;
            await root.Gate.WaitAsync(cancel).ConfigureAwait(false);
            try{mount=root.Definition;keys=QueueCredentials(mount,options,credentials);ValidateSqsSettings(mount.Source!,options with{Enabled=true},keys);}
            finally{root.Gate.Release();}
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await using var client=CreateQueueClient(mount.Source!,options,keys);var attributes=await client.AttributesAsync(deadline.Token).ConfigureAwait(false);SqsQueueClient.VerifyAttributes(options,attributes);
                return new ConnectionTestResult(true,"队列属性读取成功；未接收或确认消息，消费权限在运行时验证。请为每台机器配置独立 Standard Queue。");
            }
            catch(OperationCanceledException)when(!cancel.IsCancellationRequested){return new(false,"SQS 只读连接测试超时。");}
            catch(IOException error){return new(false,error.Message);}
        },token);
    /// <summary>按挂载保存非秘密配置和独立 DPAPI 凭据，失败恢复原凭据；停止旧订阅后再启动新设置，不持根锁等待。</summary>
    public Task SetSqsConfigurationAsync(Guid id,SqsConfiguration options,SourceCredentials? credentials=null,CancellationToken token=default)
        =>OwnAsync(async cancel=>
        {
            var root=Root(id);await root.Gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if(root.Definition.Source is not {Kind:FileSourceKind.S3} source||root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("当前挂载不能更改 SQS 设置。");
                var keys=QueueCredentials(root.Definition,options,credentials);ValidateSqsSettings(source,options,keys);var previous=SqsStore().Load(id);
                try
                {
                    if(options.CredentialMode==SqsCredentialMode.SeparateKeys)SqsStore().Save(id,keys);
                    var definition=root.Definition with{Sqs=options};_repository.SaveDefinition(definition);root.Definition=definition;
                }
                catch{SqsStore().Save(id,previous);throw;}
                if(options.CredentialMode!=SqsCredentialMode.SeparateKeys)try{SqsStore().Delete(id);}catch(IOException e){_logger.Queue("SQS 配置","Warning",e.GetType().Name);}
            }
            finally{root.Gate.Release();}
            RequestRemoteStop(root);await StopRemoteAsync(root).ConfigureAwait(false);StartRemoteSync(root);Notify();
        },token);
    private void StartRemoteEvents(Runtime root,RemoteSyncRuntime runtime)
    {
        if(runtime.QueueStarted||runtime.Stopping||IsStopping||!root.Definition.RemoteAutoDetectEnabled||root.Definition.Sqs is not {Enabled:true} options||root.Source is not S3FileSource)return;
        runtime.QueueStarted=true;runtime.QueueWork=Task.Run(()=>QueueSubscriptionAsync(root,runtime,options));
    }
    private async Task QueueSubscriptionAsync(Runtime root,RemoteSyncRuntime runtime,SqsConfiguration options)
    {
        try
        {
            root.Detection=root.Detection with{QueueMode="正在连接",QueueError=null};Notify();var mount=root.Definition;
            await using var lease=await QueueEvents.SubscribeAsync(new(mount,runtime.Engine.Scope,time=>
            {
                if(!RemoteSessionCurrent(root,runtime))return;root.Detection=root.Detection with{LastEventUtc=time,PendingHints=_repository.S3EventHints(mount.Id,runtime.Engine.Scope).Count(x=>x.State=="Pending")};Notify();runtime.Wake();
            },error=>{if(RemoteSessionCurrent(root,runtime)){root.Detection=root.Detection with{QueueError=error,QueueMode=error is null?"正在监听":"故障，完整对账继续"};Notify();}}),options,QueueCredentials(mount,options),runtime.Stop.Token).ConfigureAwait(false);
            root.Detection=root.Detection with{QueueMode="正在监听"};Notify();await Task.Delay(Timeout.InfiniteTimeSpan,runtime.Stop.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException)when(runtime.Stop.IsCancellationRequested){}
        catch(Exception error)
        {if(RemoteSessionCurrent(root,runtime)){root.Detection=root.Detection with{QueueMode="故障，完整对账继续",QueueError=error is IOException or ArgumentException?error.Message:"SQS 订阅失败。"};Notify();}}
    }
}
