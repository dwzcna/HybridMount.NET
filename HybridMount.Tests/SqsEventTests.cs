using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>队列提示的可靠落库、确认边界、乱序和共享 receiver；本地队列不会连接 AWS。</summary>
public sealed class SqsEventTests
{
    internal static readonly FileSourceConfiguration Aws=new(FileSourceKind.S3,"https://s3.us-east-1.amazonaws.com","test-bucket","remote","us-east-1");
    internal static readonly SqsConfiguration Queue=new(true,"https://sqs.us-east-1.amazonaws.com/123456789012/hybrid-test","us-east-1");
    internal static string Body(string key="remote/文件.bin",string sequence="1",string bucket="test-bucket",string operation="ObjectCreated:Put")=>JsonSerializer.Serialize(new{Records=new[]{new{eventVersion="2.1",eventSource="aws:s3",eventName=operation,awsRegion="us-east-1",s3=new{bucket=new{name=bucket},@object=new{key=Uri.EscapeDataString(key).Replace("%20","+"),sequencer=sequence}}}}});
    private static MountRepository Repository(S3Fixture fixture)=>new(Path.Combine(fixture.Area,"sqs-data"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));
    private static MountDefinition Mount(Guid? id=null)=>new(id??Guid.NewGuid(),"queue","D:\not-a-registered-root",Source:Aws,Sqs:Queue);
    [Theory]
    [InlineData("目录/中文 space + %2B.bin")][InlineData("百分号%/a+b c.txt")][InlineData("literal%2F.txt")]
    public void EventKeysAreDecodedExactlyOnce(string key)=>Assert.Equal(key,S3EventParser.Parse(Body(key)).Records.Single().Key);
    [Theory]
    [InlineData("%")][InlineData("%GG")][InlineData("%C0%AF")]
    public void MalformedEncodingIsRejectedBeforeAnyRecordsAreAccepted(string encoded)
    {
        var body=Body("placeholder").Replace("placeholder",encoded);Assert.Throws<IOException>(()=>S3EventParser.Parse(body));
    }
    [Fact]
    public void QueueValidationRejectsCrossRegionFifoAndNonAwsEndpoints()
    {
        Assert.Equal(Queue.QueueUrl,SqsQueueClient.Validate(Aws,Queue).AbsoluteUri);
        Assert.Throws<ArgumentException>(()=>SqsQueueClient.Validate(Aws,Queue with{Region="us-west-2"}));
        Assert.Throws<ArgumentException>(()=>SqsQueueClient.Validate(Aws,Queue with{QueueUrl=Queue.QueueUrl+".fifo"}));
        Assert.Throws<ArgumentException>(()=>SqsQueueClient.Validate(Aws with{Endpoint="https://example.r2.cloudflarestorage.com"},Queue));
        Assert.Throws<ArgumentException>(()=>SqsQueueClient.Validate(Aws,Queue with{QueueUrl="https://user:secret@sqs.us-east-1.amazonaws.com/123456789012/hybrid-test"}));
    }
    [Fact]
    public async Task MultipleMountsShareOneReceiverAndAllHintsPrecedeAcknowledgment()
    {
        await using var fixture=new S3Fixture();var repository=Repository(fixture);var queue=new FakeQueue();var clients=0;
        await using var hub=new SqsEventHub(repository,(_,_,_)=>{clients++;return queue;});var a=Mount();var b=Mount();var wakes=0;
        await using var first=await hub.SubscribeAsync(new(a,"a",_=>Interlocked.Increment(ref wakes),_=>{}),Queue,S3Fixture.Credentials,default);
        await using var second=await hub.SubscribeAsync(new(b,"b",_=>Interlocked.Increment(ref wakes),_=>{}),Queue,S3Fixture.Credentials,default);
        queue.OnAck=_=>{Assert.Single(repository.S3EventHints(a.Id,"a"));Assert.Single(repository.S3EventHints(b.Id,"b"));return Task.CompletedTask;};
        queue.Add(new("message",Body(),"ephemeral"));await queue.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1,clients);Assert.Equal(2,wakes);Assert.Equal(1,queue.Attributes);Assert.Equal(1,queue.Acks);
        Assert.DoesNotContain("ephemeral",System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(repository.DataDirectory,"mounts.db"))));
    }
    [Fact]
    public async Task DuplicateAndOlderSequencesDoNotReplaceLatestHint()
    {
        await using var fixture=new S3Fixture();var repository=Repository(fixture);var id=Guid.NewGuid();
        S3EventHint Hint(string seq,string scope="scope")=>new(Guid.NewGuid().ToString("N"),id,scope,"path","bucket",seq,"ObjectCreated:Put");
        repository.PersistSqsMessage("queue","one",[Hint("F"),Hint("10")]);Assert.Single(repository.S3EventHints(id,"scope"),x=>x.State=="Pending");
        var restarted=new MountRepository(repository.DataDirectory);restarted.PersistSqsMessage("queue","one",[Hint("11")]);restarted.PersistSqsMessage("queue","old",[Hint("2")]);
        Assert.Equal("10",restarted.S3EventHints(id,"scope").Single(x=>x.State=="Pending").Sequence);
        restarted.PersistSqsMessage("queue","other-account",[Hint("1","other")]);Assert.Single(restarted.S3EventHints(id,"other"));
    }
    [Fact]
    public async Task LostAcknowledgmentRedeliveryDoesNotDuplicateDurableHints()
    {
        await using var fixture=new S3Fixture();var repository=Repository(fixture);var queue=new FakeQueue();var mount=Mount();var attempts=0;
        await using var hub=new SqsEventHub(repository,(_,_,_)=>queue){RetryDelayForTests=_=>TimeSpan.FromMilliseconds(10)};
        await using var lease=await hub.SubscribeAsync(new(mount,"scope",_=>{},_=>{}),Queue,S3Fixture.Credentials,default);
        var message=new SqsMessage("same-message",Body(),"receipt-one");queue.OnAck=m=>{if(Interlocked.Increment(ref attempts)==1){queue.Add(m with{Receipt="receipt-two"});throw new IOException("ack response lost");}return Task.CompletedTask;};
        queue.Add(message);await queue.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Single(repository.S3EventHints(mount.Id,"scope"));Assert.Equal(2,attempts);
    }
    [Fact]
    public async Task MalformedMessageIsNotAcknowledgedAndStopCancelsLongPoll()
    {
        await using var fixture=new S3Fixture();var repository=Repository(fixture);var queue=new FakeQueue();var mount=Mount();var error=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub=new SqsEventHub(repository,(_,_,_)=>queue);var lease=await hub.SubscribeAsync(new(mount,"scope",_=>{},message=>{if(message is not null)error.TrySetResult();}),Queue,S3Fixture.Credentials,default);
        queue.Add(new("invalid","{}","receipt"));await error.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,queue.Acks);Assert.Empty(repository.S3EventHints(mount.Id,"scope"));
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));await hub.DisposeAsync();Assert.Equal(1,queue.Disposals);
    }
    [Fact]
    public async Task DeleteNotificationRequeriesCurrentS3VersionInsteadOfDeletingFromMessage()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var repository=Repository(fixture);var mount=Guid.NewGuid();var engine=new RemoteSyncEngine(mount,"scope",source,source,repository);
        await engine.PollAsync(true,TimeSpan.Zero,default);var before=repository.RemotePending(mount,"scope").Count;
        repository.PersistSqsMessage("queue","delete-old-version",[new(Guid.NewGuid().ToString("N"),mount,"scope","文件.bin","test-bucket","1","ObjectRemoved:DeleteMarkerCreated")]);
        await engine.ObserveEventHintsAsync(default);Assert.Equal(before,repository.RemotePending(mount,"scope").Count);Assert.Equal("Verified",repository.S3EventHints(mount,"scope").Single().State);
        Assert.DoesNotContain(repository.RemotePending(mount,"scope"),x=>x.Entry is null);
        fixture.Objects.TryRemove("remote/文件.bin",out _);repository.PersistSqsMessage("queue","actually-deleted",[new(Guid.NewGuid().ToString("N"),mount,"scope","文件.bin","test-bucket","2","ObjectRemoved:Delete")]);
        await engine.ObserveEventHintsAsync(default);Assert.Contains(repository.RemotePending(mount,"scope"),x=>x.Path=="文件.bin"&&x.Entry is null);
    }
    internal sealed class FakeQueue : ISqsQueueClient
    {
        private readonly Channel<SqsMessage> _channel=Channel.CreateUnbounded<SqsMessage>();
        public int Attributes,Receives,Acks,Disposals;
        public Func<SqsMessage,Task>? OnAck;
        public TaskCompletionSource Acknowledged=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Add(SqsMessage message)=>_channel.Writer.TryWrite(message);
        public Task<IReadOnlyDictionary<string,string>> AttributesAsync(CancellationToken token){Interlocked.Increment(ref Attributes);return Task.FromResult<IReadOnlyDictionary<string,string>>(new Dictionary<string,string>{{"QueueArn","arn:aws:sqs:us-east-1:123456789012:hybrid-test"},{"FifoQueue","false"}});}
        public async Task<IReadOnlyList<SqsMessage>> ReceiveAsync(CancellationToken token){Interlocked.Increment(ref Receives);return [await _channel.Reader.ReadAsync(token)];}
        public async Task AcknowledgeAsync(SqsMessage message,CancellationToken token){if(OnAck is not null)await OnAck(message);Interlocked.Increment(ref Acks);Acknowledged.TrySetResult();}
        public ValueTask DisposeAsync(){Interlocked.Increment(ref Disposals);return ValueTask.CompletedTask;}
    }
}
