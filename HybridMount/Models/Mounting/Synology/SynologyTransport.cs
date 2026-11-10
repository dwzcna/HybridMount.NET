using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace HybridMount.Models.Mounting.Synology;

/// <summary>认证、API 协商与安全诊断；失败锁存只随显式新连接恢复，不在后台反复尝试密码。</summary>
public sealed partial class SynologyFileSource
{
    private sealed record Api(string Path,int Version,bool Json);
    private readonly Dictionary<string,Api> _apis=new(StringComparer.Ordinal);
    private string? _sid,_synoToken,_certificateSha256,_rejectedCertificate;
    private SourceCredentials _credentials;
    private string? _otpCode;
    private Action<SourceCredentials>? _saveCredentials;
    private MountQueueBinding? _queueBinding;
    private readonly HttpClient _http;
    private const string Additional="[\"size\",\"time\",\"type\",\"real_path\"]";
    private static readonly Dictionary<string,int[]> Supported=new(StringComparer.Ordinal)
    {
        ["SYNO.API.Auth"]=[6,5,4,3],["SYNO.FileStation.List"]=[2],["SYNO.FileStation.MD5"]=[2],
        ["SYNO.FileStation.Download"]=[2],["SYNO.FileStation.Upload"]=[3,2],
        ["SYNO.FileStation.CreateFolder"]=[2],["SYNO.FileStation.Delete"]=[2]
    };
    private HttpMessageHandler CreateHandler(string? pin)=>new HttpClientHandler
    {
        AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,
        ServerCertificateCustomValidationCallback=(_,certificate,_,errors)=>
        {
            if(certificate is null)return false;
            var fingerprint=Convert.ToHexString(SHA256.HashData(certificate.RawData));_certificateSha256=fingerprint;
            var accepted=AcceptCertificate(certificate,errors,pin);_rejectedCertificate=accepted?null:fingerprint;return accepted;
        }
    };
    internal static bool AcceptCertificate(X509Certificate2? certificate,SslPolicyErrors errors,string? pin)
        =>certificate is not null&&(errors==SslPolicyErrors.None||NormalizeFingerprint(pin) is {} expected&&Convert.ToHexString(SHA256.HashData(certificate.RawData))==expected);
    private static SourceConnectionException Failure(string stage,ConnectionFailureKind kind,string message,int? code=null,bool transient=false,int? http=null)
        =>new(new(stage,kind,http,code?.ToString(System.Globalization.CultureInfo.InvariantCulture),null,null,message,Transient:transient));
    private static Exception ApiFailure(int code)=>code switch
    {
        408=>new FileNotFoundException("群晖文件或目录不存在。"),
        414 or 1805=>new SourceConflictException("群晖目标已存在，新建不能覆盖已有内容。"),
        105 or 403 or 404 or 405 or 407 or 411=>Failure("File Station",ConnectionFailureKind.Permission,"群晖拒绝当前文件操作，请核对账号权限。",code),
        402 or 109 or 110 or 111 or 117 or 118=>Failure("File Station",ConnectionFailureKind.Service,"群晖服务忙，请稍后重试。",code,true),
        _=>Failure("File Station",ConnectionFailureKind.Service,"群晖 File Station 明确拒绝请求。",code)
    };
    private SourceConnectionException AuthenticationFailure(int code)
    {
        lock(_authenticationSync)return _authenticationFailure??=Failure("登录",ConnectionFailureKind.Authentication,
            "群晖需要用户重新认证；请核对账号、权限、二步验证码或设备令牌后重连。",code);
    }
    private Api Descriptor(string api)=>_apis.TryGetValue(api,out var descriptor)?descriptor:throw new NotSupportedException("群晖未提供兼容的 File Station API："+api);
    private Uri ApiAddress(string api)=>new(_endpoint,"webapi/"+Descriptor(api).Path);
    private IDisposable? Share(string resource)
        =>_queueBinding is {} binding&&TasksQueueService.CurrentTask is {} task?binding.Queue.ShareResource(task,"synology:"+_connection+":"+resource):null;
    private async Task DiscoverAsync(CancellationToken token)
    {
        JsonElement info;
        var query=new Dictionary<string,string>{["api"]="SYNO.API.Info",["version"]="1",["method"]="query",["query"]=string.Join(',',Supported.Keys)};
        try{info=await SendJsonAsync(new Uri(_endpoint,"webapi/query.cgi"),query,false,token).ConfigureAwait(false);}
        catch(Exception error)when(error is SourceRequestRejectedException{HttpStatus:404} or SynologyApiException{Code:102 or 103})
        {info=await SendJsonAsync(new Uri(_endpoint,"webapi/entry.cgi"),query,false,token).ConfigureAwait(false);}
        var found=new Dictionary<string,Api>(StringComparer.Ordinal);
        foreach(var supported in Supported)
        {
            if(!info.TryGetProperty(supported.Key,out var item))continue;
            var path=item.GetProperty("path").GetString();
            if(string.IsNullOrEmpty(path)||path.Contains('\\')||path.Contains(':')||path.StartsWith('/')||path.Split('/').Any(x=>x is "" or "." or "..")||path.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '/' or '.' or '_' or '-')))
                throw new IOException("群晖返回的 API 路径无效。");
            var min=item.GetProperty("minVersion").GetInt32();var max=item.GetProperty("maxVersion").GetInt32();
            if(min<1||max<min)throw new IOException("群晖返回的 API 版本无效。");
            var version=supported.Value.FirstOrDefault(value=>value>=min&&value<=max);
            if(version!=0)found.Add(supported.Key,new(path,version,item.TryGetProperty("requestFormat",out var format)&&format.GetString()=="JSON"));
        }
        foreach(var required in new[]{"SYNO.API.Auth","SYNO.FileStation.List","SYNO.FileStation.MD5","SYNO.FileStation.Download"})
            if(!found.ContainsKey(required))throw new NotSupportedException("群晖缺少安全读取所需的兼容接口："+required);
        token.ThrowIfCancellationRequested();
        lock(_authenticationSync)foreach(var item in found)_apis[item.Key]=item.Value;
    }
    private async Task<string> EnsureSessionAsync(CancellationToken token,string? expired=null)
    {
        token.ThrowIfCancellationRequested();
        lock(_authenticationSync)
        {
            if(_authenticationFailure is {} failed)throw failed;
            if(expired is not null&&_sid==expired){_sid=null;_synoToken=null;}
            if(_sid is {} current)return current;
        }
        using var shared=Share("login");
        return await _logins.RunAsync(0,LoginAsync,token,Share("login")).ConfigureAwait(false);
    }
    private async Task<string> LoginAsync(CancellationToken token)
    {
        lock(_authenticationSync){if(_authenticationFailure is {} failed)throw failed;if(_sid is {} current)return current;}
        if(_apis.Count==0)await DiscoverAsync(token).ConfigureAwait(false);
        var version=Descriptor("SYNO.API.Auth").Version;
        var login=new Dictionary<string,string>{["api"]="SYNO.API.Auth",["version"]=Number(version),["method"]="login",["account"]=_credentials.UserName,["passwd"]=_credentials.Password,["session"]="FileStation",["format"]="sid"};
        if(version>=6)
        {
            login["enable_device_token"]="yes";login["enable_syno_token"]="yes";login["device_name"]="HybridMount";
            if(_credentials.SessionToken.Length!=0&&string.IsNullOrEmpty(_otpCode))login["device_id"]=_credentials.SessionToken;
        }
        if(!string.IsNullOrEmpty(_otpCode))login["otp_code"]=_otpCode;
        JsonElement data;
        try{data=await SendJsonAsync(ApiAddress("SYNO.API.Auth"),login,false,token).ConfigureAwait(false);}
        catch(SynologyApiException error){throw AuthenticationFailure(error.Code);}
        catch(SourceRequestRejectedException error)when(error.HttpStatus is 401 or 403){throw AuthenticationFailure(error.HttpStatus);}
        finally{_otpCode=null;}
        token.ThrowIfCancellationRequested();
        var sid=data.GetProperty("sid").GetString();if(string.IsNullOrEmpty(sid))throw new IOException("群晖登录未返回会话标识。");
        _synoToken=data.TryGetProperty("synotoken",out var csrf)?csrf.GetString():null;
        if(version>=6&&data.TryGetProperty("did",out var device)&&device.GetString() is {Length:>0} did)
        {
            _credentials=_credentials with{SessionToken=did};_saveCredentials?.Invoke(_credentials);
        }
        lock(_authenticationSync)_sid=sid;return sid;
    }
    private Dictionary<string,string> Fields(string api,string method,Dictionary<string,string> values,string sid)
    {
        var descriptor=Descriptor(api);var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var pair in values)
        {
            // 数组、布尔及数字保留 JSON 表达；其他业务参数按协商格式编码，认证控制参数保持原样。
            var value=pair.Value;
            var scalar=pair.Key is "offset" or "limit" or "mtime" or "crtime" or "atime" or "create_parents" or "force_parent" or "recursive";
            if(descriptor.Json&&!scalar&&!value.StartsWith('[')&&!value.StartsWith('{'))value=JsonSerializer.Serialize(value);
            result[pair.Key]=value;
        }
        result["api"]=api;result["version"]=Number(descriptor.Version);result["method"]=method;result["_sid"]=sid;
        if(_synoToken is {} csrf)result["SynoToken"]=csrf;return result;
    }
    private async Task<JsonElement> ApiAsync(string api,string method,Dictionary<string,string> fields,bool mutation,CancellationToken token)
    {
        for(var attempt=0;;attempt++)
        {
            var sid=await EnsureSessionAsync(token).ConfigureAwait(false);
            try{return await SendJsonAsync(ApiAddress(api),Fields(api,method,fields,sid),mutation,token).ConfigureAwait(false);}
            catch(SynologyApiException error)when(error.Code is 106 or 107 or 119)
            {
                if(mutation||attempt!=0)throw AuthenticationFailure(error.Code);
                await EnsureSessionAsync(token,sid).ConfigureAwait(false);
            }
            catch(SynologyApiException error){throw ApiFailure(error.Code);}
        }
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,bool mutation,CancellationToken token,CancellationToken? admission=null)
    {
        token.ThrowIfCancellationRequested();
        try{return await MountingHttpScheduler.SendAsync(_queueBinding,MountingPriority.Observation,
            cancel=>{cancel.ThrowIfCancellationRequested();return _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);},admission??token).ConfigureAwait(false);}
        catch(HttpRequestException)when(_rejectedCertificate is not null){throw Failure("TLS",ConnectionFailureKind.Certificate,"群晖 TLS 证书未通过验证，请核对本挂载证书指纹。");}
        catch(Exception error)when(error is HttpRequestException or OperationCanceledException)
        {
            if(mutation)throw new SourceOutcomeUnknownException("群晖写入",Failure("传输",ConnectionFailureKind.Network,"请求可能已发送，请先只读核对远端结果。",transient:true));
            if(error is OperationCanceledException&&token.IsCancellationRequested)throw;
            throw Failure("连接",ConnectionFailureKind.Network,"无法连接群晖 File Station 服务。",transient:true);
        }
    }
    private async Task<JsonElement> SendJsonAsync(Uri address,Dictionary<string,string> fields,bool mutation,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,address){Content=new FormUrlEncodedContent(fields)};
            using var response=await SendAsync(request,mutation,deadline.Token).ConfigureAwait(false);
            return await ParseResponseAsync(response,mutation,deadline.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested&&!mutation){throw Failure("响应",ConnectionFailureKind.Timeout,"群晖 API 响应超时。",transient:true);}
        catch(Exception error)when(mutation&&error is not(SynologyApiException or SourceOutcomeUnknownException or SourceRequestRejectedException or SourceConnectionException))
        {throw new SourceOutcomeUnknownException("群晖写入",new IOException("未取得可信的操作回执，请先只读核对远端结果。"));}
    }
    private static async Task<JsonElement> ParseResponseAsync(HttpResponseMessage response,bool mutation,CancellationToken token)
    {
        if(!response.IsSuccessStatusCode)
        {
            if(mutation&&(int)response.StatusCode>=500)throw new SourceOutcomeUnknownException("群晖写入",new IOException("服务端未确认结果。"));
            throw new SourceRequestRejectedException((int)response.StatusCode,"群晖 HTTP 请求被拒绝。");
        }
        await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer=new MemoryStream();var bytes=new byte[8192];
        while(true){var count=await stream.ReadAsync(bytes,token).ConfigureAwait(false);if(count==0)break;if(buffer.Length+count>4*1024*1024)throw new IOException("群晖 API 响应超限。");await buffer.WriteAsync(bytes.AsMemory(0,count),token).ConfigureAwait(false);}
        try
        {
            using var document=JsonDocument.Parse(buffer.ToArray());var root=document.RootElement;
            if(!root.GetProperty("success").GetBoolean())throw new SynologyApiException(root.GetProperty("error").GetProperty("code").GetInt32());
            var data=root.TryGetProperty("data",out var value)?value.Clone():JsonSerializer.SerializeToElement(new{});CheckTaskErrors(data);return data;
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {throw new IOException("群晖返回的 API 响应格式无效。");}
    }
    private static void CheckTaskErrors(JsonElement data)
    {
        if(data.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("code",out var code)&&code.GetInt32()!=0)throw new SynologyApiException(code.GetInt32());
        if(data.TryGetProperty("errors",out var errors)&&errors.ValueKind==JsonValueKind.Array&&errors.GetArrayLength()>0)throw new SynologyApiException(errors[0].GetProperty("code").GetInt32());
    }
}
/// <summary>协议内的有限错误码，绝不包含响应正文、URL 或认证参数。</summary>
internal sealed class SynologyApiException(int code):IOException($"群晖 API 返回错误（代码 {code}）。") {internal int Code {get;}=code;}
