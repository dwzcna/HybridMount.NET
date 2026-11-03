using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>协议内按职责拆分的 Transport 实现；公开入口与版本字段保持兼容。</summary>
public sealed partial class S3FileSource
{
    private readonly string _diagnosticContext;
    private sealed class ResponseHints { internal string? Region,RequestId; internal bool Certificate; }
    private static readonly AsyncLocal<ResponseHints?> Hints=new();
    public S3FileSource(FileSourceConfiguration options, SourceCredentials credentials, string workDirectory, long multipartThreshold = 16L * 1024 * 1024, IAmazonS3? client = null)
    {
        var endpoint = ValidateOptions(options);
        _diagnosticContext=$"SigV4；签名区域 {options.Region}"+(options.Region=="us-east-1"?"（默认值，尚待服务确认）":"（配置值）")+
            $"；寻址 {(options.PathStyle?"Path-Style":"Virtual-Host")}；凭据来源 {(options.UseDefaultCredentials?"SDK 默认凭据链":"表单／DPAPI 访问密钥")}";

        _remoteSyncIdentity=string.Join("\0",endpoint.AbsoluteUri,options.Bucket,options.Prefix,options.UseDefaultCredentials?"default":credentials.AccessKey);
        _bucket = options.Bucket; _prefix = options.Prefix.Trim('/').Length == 0 ? "" : RelativePaths.Normalize(options.Prefix.Trim('/')) + "/";
        _work = workDirectory; _multipartThreshold = Math.Max(5L * 1024 * 1024, multipartThreshold);
        if (client is not null) { _client = client; return; }
        var config = new AmazonS3Config { ServiceURL = endpoint.AbsoluteUri.TrimEnd('/'), AuthenticationRegion = options.Region,
            ForcePathStyle = options.PathStyle, UseHttp = endpoint.Scheme == "http", MaxErrorRetry = 0,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            HttpClientFactory = new NoRedirectHttpFactory(), CacheHttpClient = false, DisableLogging = true };
        if (options.UseDefaultCredentials) _client = new AmazonS3Client(config);
        else
        {
            if (string.IsNullOrWhiteSpace(credentials.AccessKey) || string.IsNullOrWhiteSpace(credentials.SecretKey)) throw new ArgumentException("请填写 S3 访问密钥或选择 SDK 默认凭据链。");
            AWSCredentials identity = credentials.SessionToken.Length == 0 ? new BasicAWSCredentials(credentials.AccessKey, credentials.SecretKey)
                : new SessionAWSCredentials(credentials.AccessKey, credentials.SecretKey, credentials.SessionToken);
            _client = new AmazonS3Client(identity, config);
        }
    }
    private sealed class NoRedirectHttpFactory : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig config) => new(new DiagnosticHandler(new SocketsHttpHandler { AllowAutoRedirect = false,
            MaxConnectionsPerServer = 4, ConnectTimeout = TimeSpan.FromSeconds(20) })) { Timeout = TimeSpan.FromMinutes(2) };
    }
    /// <summary>仅保留有限的错误字段；区域不通过 SDK 私有字段读取，也不自动改写配置。</summary>
    private sealed class DiagnosticHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            HttpResponseMessage response;
            try { response=await base.SendAsync(request,token).ConfigureAwait(false); }
            catch(HttpRequestException error)
            {
                if(Hints.Value is {} failed && (error.HttpRequestError==HttpRequestError.SecureConnectionError || error.InnerException is System.Security.Authentication.AuthenticationException))failed.Certificate=true;
                throw;
            }
            if(Hints.Value is {} hints)
            {
                if(response.Headers.TryGetValues("x-amz-bucket-region",out var regions))hints.Region=regions.FirstOrDefault();
                if(response.Headers.TryGetValues("x-amz-request-id",out var requests))hints.RequestId=requests.FirstOrDefault();
                if(!response.IsSuccessStatusCode && response.Content.Headers.ContentLength is >0 and <=65536)
                {
                    try
                    {
                        await response.Content.LoadIntoBufferAsync(65536,token).ConfigureAwait(false);
                        var bytes=await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                        using var reader=System.Xml.XmlReader.Create(new MemoryStream(bytes),new System.Xml.XmlReaderSettings
                        {DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});
                        var xml=System.Xml.Linq.XDocument.Load(reader);
                        hints.Region??=xml.Root?.Elements().FirstOrDefault(e=>e.Name.LocalName=="Region")?.Value;
                    }
                    catch(System.Xml.XmlException) { }
                }
            }
            return response;
        }
    }
    /// <summary>不输出 SDK 原始异常、服务端 XML 或 URL；修改请求不自动重试。</summary>
    private async Task<T> Call<T>(Func<Task<T>> action,CancellationToken token,bool mutation=false,[CallerMemberName]string stage="") where T:class
        => await CallCore(action,token,mutation,ExpectedResult.None,stage).ConfigureAwait(false)??throw new IOException("S3 缺少响应。");
    [Flags] private enum ExpectedResult { None=0,Missing=1,PreconditionRejected=2 }
    /// <summary>仅内部探测可将预期的不存在或条件拒绝作为空结果；真实请求保持原异常契约。</summary>
    private async Task<T?> CallCore<T>(Func<Task<T>> action,CancellationToken token,bool mutation,ExpectedResult expected,[CallerMemberName]string stage="") where T:class
    {
        token.ThrowIfCancellationRequested();
        var previousHints=Hints.Value;var hints=new ResponseHints();Hints.Value=hints;
        try { return await action().ConfigureAwait(false); }
        catch(AmazonS3Exception error) when((expected&ExpectedResult.Missing)!=0&&error.StatusCode==HttpStatusCode.NotFound&&error.ErrorCode!="NoSuchBucket") {return null;}
        catch(AmazonS3Exception error) when((expected&ExpectedResult.PreconditionRejected)!=0&&error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict) {return null;}
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        { throw new SourceConflictException("S3 对象版本或目标创建条件不满足。"); }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode != "NoSuchBucket" && stage != nameof(ProbeAsync))
        { throw new FileNotFoundException("S3 对象或桶不存在。"); }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException or IOException or OperationCanceledException)
        {
            if (mutation && (error is not AmazonS3Exception service || (int)service.StatusCode >= 500 || (int)service.StatusCode == 0))
                throw new SourceOutcomeUnknownException("S3 修改", new SourceConnectionException(Diagnose(error,stage,hints)));
            if(error is OperationCanceledException&&token.IsCancellationRequested)throw;
            throw new SourceConnectionException(Diagnose(error,stage,hints));
        }
        finally { Hints.Value=previousHints; }
    }
    public async Task ProbeAsync(CancellationToken token = default)
    {
        var page=await Call(() => _client.ListObjectsV2Async(new() { BucketName = _bucket, Prefix = _prefix, Delimiter = "/", MaxKeys = 1 }, token), token).ConfigureAwait(false);
        if(page.HttpStatusCode!=HttpStatusCode.OK || page.IsTruncated is null)
            throw new SourceConnectionException(new(nameof(ProbeAsync),ConnectionFailureKind.Service,(int)page.HttpStatusCode,null,null,null,"服务未返回完整的 S3 ListObjectsV2 响应；"+_diagnosticContext));

    }
    /// <summary>将 SDK 错误转换为有限字段；不读取 ResponseBody、Message 或授权信息。</summary>
    private ConnectionDiagnostic Diagnose(Exception error,string stage,ResponseHints hints)
    {
        static string? Field(string? value,int max=160) => !string.IsNullOrEmpty(value) && value.Length<=max &&
            value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '/' or '+' or '=' or '.')?value:null;
        var service=error as AmazonS3Exception;var status=service is null?0:(int)service.StatusCode;
        var code=Field(service?.ErrorCode);var kind=ConnectionFailureKind.Service;
        var explanation="S3 服务返回非预期响应，请依据服务码与请求 ID 排查。";
        if(code is "SignatureDoesNotMatch" or "AuthorizationHeaderMalformed" or "InvalidRegion" || status is 301 or 307)
        {kind=ConnectionFailureKind.Signature;explanation="签名或签名上下文不匹配，请核对区域、Path-Style、路径及系统时间。";}
        else if(status==401 || code is "InvalidAccessKeyId" or "ExpiredToken" or "InvalidToken" or "TokenRefreshRequired")
        {kind=ConnectionFailureKind.Authentication;explanation="认证未通过；请核对凭据来源与有效期，当前响应不能单独确定密钥或区域是否正确。";}
        else if(status==403)
        {kind=ConnectionFailureKind.Permission;explanation="访问被拒绝，请核对桶及前缀权限、策略或服务端认证约束。";}
        else if(status==404 || code=="NoSuchBucket")
        {kind=ConnectionFailureKind.NotFound;explanation="桶或请求资源不存在，请核对接入点与桶名。";}
        else if(status==0)
        {
            var certificate=hints.Certificate;
            for(Exception? nested=error;nested is not null;nested=nested.InnerException)
                if(nested is System.Security.Authentication.AuthenticationException || nested is HttpRequestException { HttpRequestError:HttpRequestError.SecureConnectionError })certificate=true;
            kind=certificate?ConnectionFailureKind.Certificate:ConnectionFailureKind.Network;
            explanation=certificate?"TLS 或证书验证失败，请核对主机名与证书链。":error is OperationCanceledException?"HTTP 请求超时，请检查网络和服务响应。":"请求未获得 HTTP 响应，请核对网络、DNS、代理及凭据来源。";
        }
        return new(stage,kind,status==0?null:status,code,Field(service?.RequestId)??Field(hints.RequestId),Field(hints.Region,64),explanation+"；"+_diagnosticContext);
    }

}
