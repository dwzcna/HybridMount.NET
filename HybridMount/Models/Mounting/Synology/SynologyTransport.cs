using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace HybridMount.Models.Mounting.Synology;

public sealed partial class SynologyFileSource
{
    private sealed record Api(string Path,int Version);
    private readonly Dictionary<string,Api> _apis=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionGate=new(1,1);
    private string? _sid,_certificateSha256,_rejectedCertificate;
    private SourceCredentials _credentials;
    private string? _otpCode;
    private Action<SourceCredentials>? _saveCredentials;
    private MountQueueBinding? _queueBinding;
    private readonly HttpClient _http;
    private const string Additional="[\"size\",\"time\",\"type\",\"real_path\"]";

    private HttpMessageHandler CreateHandler(string? pin)=>new HttpClientHandler
    {
        AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,
        ServerCertificateCustomValidationCallback=(_,certificate,_,errors)=>
        {
            if(certificate is null)return false;
            var fingerprint=Convert.ToHexString(SHA256.HashData(certificate.RawData));
            _certificateSha256=fingerprint;
            var accepted=AcceptCertificate(certificate,errors,pin);
            _rejectedCertificate=accepted?null:fingerprint;
            return accepted;
        }
    };
    internal static bool AcceptCertificate(X509Certificate2? certificate,SslPolicyErrors errors,string? pin)
        =>certificate is not null&&(errors==SslPolicyErrors.None||NormalizeFingerprint(pin) is {} expected&&Convert.ToHexString(SHA256.HashData(certificate.RawData))==expected);
    private Api Descriptor(string api)=>_apis.TryGetValue(api,out var descriptor)?descriptor:throw new NotSupportedException("群晖未提供所需的 File Station API："+api);

    private Uri ApiAddress(string api)
    {
        return new Uri(_endpoint,"webapi/"+Descriptor(api).Path);
    }

    private async Task<string> EnsureSessionAsync(CancellationToken token,string? expired=null)
    {
        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_sid is not null&&(expired is null||_sid!=expired))return _sid;
            if(_apis.Count==0)
            {
                var info=await SendJsonAsync(new Uri(_endpoint,"webapi/query.cgi"),new()
                { ["api"]="SYNO.API.Info",["version"]="1",["method"]="query",["query"]="all" },false,token).ConfigureAwait(false);
                var discovered=new Dictionary<string,Api>(StringComparer.Ordinal);
                foreach(var item in info.EnumerateObject())
                {
                    var path=item.Value.GetProperty("path").GetString()!;
                    if(string.IsNullOrEmpty(path)||path.Contains('\\')||path.Contains(':')||path.StartsWith('/')||path.Split('/').Any(x=>x is "" or "." or "..")||path.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '/' or '.' or '_' or '-')))
                        throw new IOException("群晖返回的 API 路径无效。");
                    var version=item.Value.GetProperty("maxVersion").GetInt32();
                    if(version<1)throw new IOException("群晖返回的 API 版本无效。");
                    discovered.Add(item.Name,new(path,version));
                }
                foreach(var item in discovered)_apis.Add(item.Key,item.Value);
            }
            _sid=null;
            var login=new Dictionary<string,string>
            {
                ["api"]="SYNO.API.Auth",["version"]=Descriptor("SYNO.API.Auth").Version.ToString(CultureInfo.InvariantCulture),
                ["method"]="login",["account"]=_credentials.UserName,["passwd"]=_credentials.Password,
                ["session"]="FileStation",["format"]="sid",["enable_device_token"]="yes",["device_name"]="HybridMount"
            };
            // 待实测：DSM 7.x 的 did 复用及 enable_device_token 在不同认证版本下的行为。
            if(_credentials.SessionToken.Length!=0)login["device_id"]=_credentials.SessionToken;
            if(!string.IsNullOrEmpty(_otpCode))login["otp_code"]=_otpCode;
            JsonElement data;
            try{data=await SendJsonAsync(ApiAddress("SYNO.API.Auth"),login,false,token).ConfigureAwait(false);}
            catch(SynologyApiException error){throw new SynologyAuthenticationException(error.Code);}
            finally{_otpCode=null;}
            var sid=data.GetProperty("sid").GetString();
            if(string.IsNullOrEmpty(sid))throw new IOException("群晖登录未返回会话标识。");
            if(data.TryGetProperty("did",out var device)&&device.GetString() is {Length:>0} did)
            {
                _credentials=_credentials with{SessionToken=did};
                _saveCredentials?.Invoke(_credentials);
            }
            _sid=sid;
            return sid;
        }
        finally{_sessionGate.Release();}
    }

    private async Task<JsonElement> ApiAsync(string api,string method,Dictionary<string,string> fields,bool mutation,CancellationToken token)
    {
        for(var attempt=0;;attempt++)
        {
            var sid=await EnsureSessionAsync(token).ConfigureAwait(false);
            var request=new Dictionary<string,string>(fields)
            { ["api"]=api,["version"]=Descriptor(api).Version.ToString(CultureInfo.InvariantCulture),["method"]=method,["_sid"]=sid };
            JsonElement result;
            try{result=await SendJsonAsync(ApiAddress(api),request,mutation,token).ConfigureAwait(false);}
            catch(SynologyApiException error)when(error.Code is 106 or 107 or 119)
            {
                if(attempt!=0)throw new SynologyAuthenticationException(error.Code);
                await EnsureSessionAsync(token,sid).ConfigureAwait(false);
                continue;
            }
            if(mutation)CheckTaskErrors(result);return result;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,bool mutation,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.Content is MultipartFormDataContent?TimeSpan.FromHours(24):TimeSpan.FromSeconds(120));
        try
        {
            return await MountingHttpScheduler.SendAsync(_queueBinding,MountingPriority.Metadata,
                cancel=>_http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancel),deadline.Token).ConfigureAwait(false);
        }
        catch(HttpRequestException)when(_rejectedCertificate is {} rejected){throw new SynologyCertificateException(rejected);}
        catch(Exception error)when(error is HttpRequestException or OperationCanceledException)
        {
            if(mutation)throw new SourceOutcomeUnknownException("群晖写入",new IOException("请求发送后连接中断或取消；请核对远端结果。"));
            if(error is OperationCanceledException)throw;
            throw new IOException("无法连接群晖 File Station 服务。");
        }
    }

    private async Task<JsonElement> SendJsonAsync(Uri address,Dictionary<string,string> fields,bool mutation,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(120));
        token=deadline.Token;
        try
        {
            for(var attempt=0;;attempt++)
            {
                using var request=new HttpRequestMessage(HttpMethod.Post,address){Content=new FormUrlEncodedContent(fields)};
                using var response=await SendAsync(request,mutation,token).ConfigureAwait(false);
                if(!mutation&&attempt==0&&((int)response.StatusCode is 429 or 502 or 503 or 504))
                {response.Dispose();await Task.Delay(TimeSpan.FromMilliseconds(300),token).ConfigureAwait(false);continue;}
                return await ParseResponseAsync(response,mutation,token).ConfigureAwait(false);
            }
        }
        catch(Exception error)when(mutation&&error is not (SynologyApiException or SynologyAuthenticationException or SynologyCertificateException or SourceOutcomeUnknownException or SourceRequestRejectedException))
        {throw new SourceOutcomeUnknownException("群晖写入",new IOException("未取得可信的操作回执，请核对远端结果。"));}
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
            if(!root.GetProperty("success").GetBoolean())
            {
                var code=root.GetProperty("error").GetProperty("code").GetInt32();
                throw new SynologyApiException(code);
            }
            return root.TryGetProperty("data",out var data)?data.Clone():JsonSerializer.SerializeToElement(new{});
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {throw new IOException("群晖返回的 API 响应格式无效。");}
    }
}
