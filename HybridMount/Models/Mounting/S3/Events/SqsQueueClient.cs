using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>可替换的队列传输；测试连接只调用 Attributes，不借助接收确认来探测权限。</summary>
internal interface ISqsQueueClient : IAsyncDisposable
{
    Task<IReadOnlyDictionary<string,string>> AttributesAsync(CancellationToken token);
    Task<IReadOnlyList<SqsMessage>> ReceiveAsync(CancellationToken token);
    Task AcknowledgeAsync(SqsMessage message,CancellationToken token);
}
/// <summary>官方 AWS Standard SQS 客户端，SigV4、禁止重定向，错误只输出有限服务字段。</summary>
internal sealed class SqsQueueClient : ISqsQueueClient
{
    private readonly IAmazonSQS _client;private readonly string _url;
    internal SqsQueueClient(FileSourceConfiguration source,SqsConfiguration options,SourceCredentials credentials)
    {
        Validate(source,options);_url=options.QueueUrl;
        var config=new AmazonSQSConfig{RegionEndpoint=RegionEndpoint.GetBySystemName(options.Region),MaxErrorRetry=0,DisableLogging=true,CacheHttpClient=false,HttpClientFactory=new QueueHttpFactory()};
        if(options.CredentialMode==SqsCredentialMode.DefaultCredentials||options.CredentialMode==SqsCredentialMode.ReuseS3&&source.UseDefaultCredentials)_client=new AmazonSQSClient(config);
        else
        {
            if(string.IsNullOrWhiteSpace(credentials.AccessKey)||string.IsNullOrWhiteSpace(credentials.SecretKey))throw new ArgumentException("请填写队列访问密钥，或选择明确的凭据来源。");
            AWSCredentials keys=credentials.SessionToken.Length==0?new BasicAWSCredentials(credentials.AccessKey,credentials.SecretKey):new SessionAWSCredentials(credentials.AccessKey,credentials.SecretKey,credentials.SessionToken);
            _client=new AmazonSQSClient(keys,config);
        }
    }
    private sealed class QueueHttpFactory : Amazon.Runtime.HttpClientFactory
    {public override HttpClient CreateHttpClient(IClientConfig config)=>new(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(20)}){Timeout=TimeSpan.FromSeconds(45)};}
    internal static Uri Validate(FileSourceConfiguration source,SqsConfiguration options)
    {
        if(source.Kind!=FileSourceKind.S3||!Enum.IsDefined(options.CredentialMode))throw new ArgumentException("SQS 只用于 S3 挂载。");
        var endpoint=FileSourceFactory.Endpoint(source);var suffix=source.Region.StartsWith("cn-",StringComparison.Ordinal)?"amazonaws.com.cn":"amazonaws.com";
        if(options.Region!=source.Region||endpoint.Scheme!="https"||endpoint.Host!="s3."+source.Region+"."+suffix&&!(source.Region=="us-east-1"&&endpoint.Host=="s3.amazonaws.com"))
            throw new ArgumentException("首版 SQS 仅支持同区域 AWS S3 原生通知；R2 和其他兼容服务请使用定时对账。");
        if(!Uri.TryCreate(options.QueueUrl,UriKind.Absolute,out var queue)||queue.Scheme!="https"||queue.Host!="sqs."+options.Region+"."+suffix||!queue.IsDefaultPort||queue.UserInfo.Length!=0||queue.Query.Length!=0||queue.Fragment.Length!=0)
            throw new ArgumentException("请输入与 S3 同区域的 AWS Standard Queue URL，不包含账户凭据、查询或片段。");
        var parts=queue.AbsolutePath.Trim('/').Split('/');
        if(parts.Length!=2||parts[0].Length!=12||parts[0].Any(x=>!char.IsAsciiDigit(x))||parts[1].Length is <1 or >80||parts[1].Any(x=>!char.IsAsciiLetterOrDigit(x)&&x is not('_' or '-')))
            throw new ArgumentException("SQS 首版只支持 Standard Queue，不支持 FIFO 或非标准队列路径。");
        return queue;
    }
    internal static void VerifyAttributes(SqsConfiguration options,IReadOnlyDictionary<string,string> attributes)
    {
        var queue=new Uri(options.QueueUrl);var parts=queue.AbsolutePath.Trim('/').Split('/');var arn=attributes.GetValueOrDefault("QueueArn")?.Split(':');
        if(arn?.Length!=6||arn[2]!="sqs"||arn[3]!=options.Region||arn[4]!=parts[0]||arn[5]!=parts[1]||attributes.GetValueOrDefault("FifoQueue")=="true")throw new IOException("实际队列身份或类型与配置不符。");
    }
    private static async Task<T> Call<T>(Func<Task<T>> request,CancellationToken token)
    {
        try{return await request().ConfigureAwait(false);}
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(AmazonSQSException error){var code=error.ErrorCode;throw new SqsOperationException((int)error.StatusCode,code is {Length:>0 and <=80}&&code.All(x=>char.IsAsciiLetterOrDigit(x)||x is '_' or '-')?code:"Unknown");}
        catch(Exception error)when(error is HttpRequestException or AmazonServiceException or OperationCanceledException){throw new IOException("SQS 网络、凭据来源或超时故障；定时完整对账继续运行。");}
    }
    public async Task<IReadOnlyDictionary<string,string>> AttributesAsync(CancellationToken token)
    {
        var response=await Call(()=>_client.GetQueueAttributesAsync(new(){QueueUrl=_url,AttributeNames=["QueueArn","FifoQueue","RedrivePolicy","VisibilityTimeout"]},token),token).ConfigureAwait(false);
        return response.Attributes??[];
    }
    public async Task<IReadOnlyList<SqsMessage>> ReceiveAsync(CancellationToken token)
    {
        var response=await Call(()=>_client.ReceiveMessageAsync(new ReceiveMessageRequest(){QueueUrl=_url,MaxNumberOfMessages=10,WaitTimeSeconds=20,VisibilityTimeout=60},token),token).ConfigureAwait(false);
        return (response.Messages??[]).Select(x=>new SqsMessage(x.MessageId,x.Body,x.ReceiptHandle)).ToArray();
    }
    public async Task AcknowledgeAsync(SqsMessage message,CancellationToken token)
        =>_ = await Call(()=>_client.DeleteMessageAsync(new(){QueueUrl=_url,ReceiptHandle=message.Receipt},token),token).ConfigureAwait(false);
    public ValueTask DisposeAsync(){_client.Dispose();return ValueTask.CompletedTask;}
}
