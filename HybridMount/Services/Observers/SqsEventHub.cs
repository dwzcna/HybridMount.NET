using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Observers;

/// <summary>同进程同队列一个 receiver；跨机器必须使用独立队列，消息确认以所有相关挂载提示持久化为界。</summary>
internal sealed class SqsEventHub(MountRepository repository,Func<FileSourceConfiguration,SqsConfiguration,SourceCredentials,ISqsQueueClient>? factory=null,TasksQueueService? tasksQueue=null) : IAsyncDisposable
{
    internal sealed record Subscriber(MountDefinition Mount,string Scope,Action<DateTimeOffset> Wake,Action<string?> Error, Action<string>? Capability=null);
    private sealed class Group(string key,string fingerprint,ISqsQueueClient client)
    {
        public readonly string Key=key,Fingerprint=fingerprint;
        public readonly ISqsQueueClient Client=client;
        public readonly Dictionary<Guid,Subscriber> Subscribers=[];
        public readonly CancellationTokenSource Stop=new();
        public Task Work=Task.CompletedTask;
    }
    private readonly Dictionary<string,Group> _groups=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate=new(1,1);
    private bool _stopping;
    internal Func<int,TimeSpan>? RetryDelayForTests {get;set;}
    private Task? _dispose;
    private sealed class Lease(SqsEventHub owner,string key,Guid mount) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()=>Interlocked.Exchange(ref _released,1)!=0?ValueTask.CompletedTask:new(owner.RemoveAsync(key,mount));
    }
    /// <summary>预检只读取属性；接收循环属于 hub，订阅释放在根锁外等候，最后一个订阅取消长轮询。</summary>
    internal async Task<IAsyncDisposable> SubscribeAsync(Subscriber subscriber,SqsConfiguration options,SourceCredentials credentials,CancellationToken token)
    {
        var endpoint=SqsQueueClient.Validate(subscriber.Mount.Source!,options);var key=endpoint.AbsoluteUri.TrimEnd('/');
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{Default=options.CredentialMode==SqsCredentialMode.DefaultCredentials||options.CredentialMode==SqsCredentialMode.ReuseS3&&subscriber.Mount.Source!.UseDefaultCredentials,credentials.AccessKey,credentials.SecretKey,credentials.SessionToken});
        string fingerprint;try{fingerprint=Convert.ToHexString(SHA256.HashData(bytes));}finally{CryptographicOperations.ZeroMemory(bytes);}
        IAsyncDisposable result;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_stopping)throw new OperationCanceledException(token);
            _groups.TryGetValue(key,out var group);
            if(group is not null&&group.Fingerprint!=fingerprint)throw new IOException("同队列挂载需使用相同队列凭据来源；已保留定时完整对账。");
            if(group is null||group.Work.IsCompleted)
            {
                var client=(factory??((s,q,c)=>new SqsQueueClient(s,q,c)))(subscriber.Mount.Source!,options,credentials);
                try{var attributes=await client.AttributesAsync(token).ConfigureAwait(false);SqsQueueClient.VerifyAttributes(options,attributes);}
                catch{await client.DisposeAsync();throw;}
                var replacement=new Group(key,fingerprint,client);
                if(group is not null){foreach(var pair in group.Subscribers)replacement.Subscribers.Add(pair.Key,pair.Value);group.Stop.Dispose();}
                group=replacement;_groups[key]=group;group.Subscribers.Add(subscriber.Mount.Id,subscriber);group.Work=tasksQueue is null?Task.Run(()=>RunAsync(group)):tasksQueue.RunObserverAsync(new MountingObserver("sqs:"+Hash(group.Key),null,cancel=>RunAsync(group,cancel)),group.Stop.Token);
            }
            else group.Subscribers.Add(subscriber.Mount.Id,subscriber);
            result = new Lease(this,key,subscriber.Mount.Id);
        }
        finally{_gate.Release();}
        try { subscriber.Capability?.Invoke("sqs.attributes"); } catch { }
        return result;
    }
    /// <summary>从已有成功请求发布只读能力；复制订阅后释放控制锁，展示回调不能阻断消息处理。</summary>
    private async Task PublishCapabilityAsync(Group group, string id)
    {
        Subscriber[] targets;
        await _gate.WaitAsync(group.Stop.Token).ConfigureAwait(false);
        try { targets = group.Subscribers.Values.ToArray(); }
        finally { _gate.Release(); }
        foreach (var target in targets) try { target.Capability?.Invoke(id); } catch { }
    }
    private async Task RunAsync(Group group,CancellationToken observerToken=default)
    {
        using var observerStop=observerToken.UnsafeRegister(static state=>((CancellationTokenSource)state!).Cancel(),group.Stop);
        var failures=0;
        try
        {
            while(!group.Stop.IsCancellationRequested)
            {
                try
                {
                    var messages=await group.Client.ReceiveAsync(group.Stop.Token).ConfigureAwait(false);
                    await PublishCapabilityAsync(group, "sqs.receive").ConfigureAwait(false);
                    foreach(var message in messages)
                    {
                        var parsed=S3EventParser.Parse(message.Body);Subscriber[] targets;
                        await _gate.WaitAsync(group.Stop.Token).ConfigureAwait(false);
                        try
                        {
                            targets=group.Subscribers.Values.ToArray();var hints=new List<S3EventHint>();var recipients=new HashSet<Guid>();
                            foreach(var record in parsed.Records)
                                foreach(var target in targets)
                                {
                                    var source=target.Mount.Source!;var prefix=source.Prefix.Trim('/');if(prefix.Length>0)prefix+="/";
                                    if(record.Bucket!=source.Bucket||record.Region!=source.Region||!record.Key.StartsWith(prefix,StringComparison.Ordinal))continue;
                                    var path=record.Key[prefix.Length..];if(path.EndsWith('/'))path=path[..^1];if(path.Length==0)continue;
                                    RelativePaths.Normalize(path);recipients.Add(target.Mount.Id);
                                    hints.Add(new(Guid.NewGuid().ToString("N"),target.Mount.Id,target.Scope,path,record.Bucket,record.Sequence,record.EventName,ReceivedUtc:DateTimeOffset.UtcNow));
                                }
                            if(!parsed.Test&&hints.Count==0)throw new IOException("S3 消息不属于当前队列挂载，未确认；请使用专用队列并检查桶和前缀。");
                            // 所有记录先整体校验，再持久化；注销订阅与此提交使用同一短锁，不产生移除后的迟到写入。
                            repository.PersistSqsMessage(Hash(group.Key),message.Id,hints);
                            targets=targets.Where(x=>recipients.Contains(x.Mount.Id)).ToArray();
                        }
                        finally{_gate.Release();}
                        foreach(var target in targets)target.Wake(DateTimeOffset.UtcNow);
                        await group.Client.AcknowledgeAsync(message,group.Stop.Token).ConfigureAwait(false);
                        await PublishCapabilityAsync(group, "sqs.ack").ConfigureAwait(false);
                    }
                    failures=0;await ReportAsync(group,null).ConfigureAwait(false);
                    // 正常服务长轮询空响应约 20 秒；异常提前空响应也不能形成热循环。
                    if(messages.Count==0)await Task.Delay(250,group.Stop.Token).ConfigureAwait(false);
                }
                catch(OperationCanceledException)when(group.Stop.IsCancellationRequested){break;}
                catch(Exception error)
                {
                    await ReportAsync(group,error is IOException?error.Message:"SQS 后台故障；定时完整对账继续运行。").ConfigureAwait(false);
                    if(error is SqsOperationException{Status:401 or 403})break;
                    failures++;await Task.Delay(RetryDelayForTests?.Invoke(failures)??TimeSpan.FromMinutes(Math.Clamp(Math.Pow(2,Math.Min(failures-1,4))*(0.9+Random.Shared.NextDouble()*0.2),1,15)),group.Stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch(OperationCanceledException)when(group.Stop.IsCancellationRequested){}
        finally{await group.Client.DisposeAsync().ConfigureAwait(false);}
    }
    private async Task ReportAsync(Group group,string? error)
    {
        Subscriber[] subscribers;await _gate.WaitAsync().ConfigureAwait(false);try{subscribers=group.Subscribers.Values.ToArray();}finally{_gate.Release();}
        foreach(var subscriber in subscribers)subscriber.Error(error);
    }
    private async Task RemoveAsync(string key,Guid mount)
    {
        var finish=false;Group? group;await _gate.WaitAsync().ConfigureAwait(false);
        try{if(!_groups.TryGetValue(key,out group))return;group.Subscribers.Remove(mount);if(group.Subscribers.Count==0){_groups.Remove(key);await group.Stop.CancelAsync();finish=true;}}
        finally{_gate.Release();}
        if(finish){await group.Work.ConfigureAwait(false);group.Stop.Dispose();}
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public ValueTask DisposeAsync(){lock(_groups)return new(_dispose??=StopAsync());}
    private async Task StopAsync()
    {
        Group[] groups;await _gate.WaitAsync().ConfigureAwait(false);
        try{_stopping=true;groups=_groups.Values.ToArray();_groups.Clear();foreach(var group in groups)await group.Stop.CancelAsync();}
        finally{_gate.Release();}
        await Task.WhenAll(groups.Select(x=>x.Work)).ConfigureAwait(false);foreach(var group in groups)group.Stop.Dispose();
    }
}
