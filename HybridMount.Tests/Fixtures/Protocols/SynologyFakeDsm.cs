using HybridMount.Infrastructure.Files.Content;
using HybridMount.Infrastructure.Protocols.Synology;
using HybridMount.Models.Mounting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
namespace HybridMount.Tests.Fixtures.Protocols;

/// <summary>可控 DSM 响应器；仅记录协议字段，故障注入不连接外部服务。</summary>
internal sealed class SynologyFakeDsm:HttpMessageHandler
{
    internal readonly ConcurrentQueue<Dictionary<string,string>> Requests=[],Logins=[],Uploads=[];
    internal readonly ConcurrentDictionary<string,byte[]> Files=new(StringComparer.Ordinal){["/share/file.bin"]=Encoding.UTF8.GetBytes("abcdefgh")};
    private readonly ConcurrentDictionary<string,string> _md5Tasks=new(StringComparer.Ordinal);
    internal int AuthMax=17,Expire,SessionError=106,LoginError,MetadataError,Discoveries,Downloads;
    internal bool LegacyMissing,OmitWrites,IgnoreCreateOnly,SkipExisting,HoldMd5;
    internal int DiscoveryStatus;
    internal string? DropUpload;
    internal Func<byte[]>? AfterDownload;
    internal Func<HttpRequestMessage,Dictionary<string,string>,CancellationToken,Task<HttpResponseMessage?>>? Intercept;
    internal bool JsonFormat,RequireOtp;
    internal readonly ConcurrentQueue<Dictionary<string,string>> RawRequests=[];
    internal readonly TaskCompletionSource HashStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Dictionary<string,string> Fields(string text)=>text.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries)
        .Select(x=>x.Split('=',2)).ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>Uri.UnescapeDataString(x[1].Replace("+"," ")));
    internal static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Success(object data)=>Json(new{success=true,data});
    private static HttpResponseMessage Failure(int code)=>Json(new{success=false,error=new{code}});
    private object File(string path)=>new{path,isdir=path=="/share",additional=new{size=Files.TryGetValue(path,out var bytes)?bytes.Length:0,time=new{mtime=10,crtime=1},type="bin",real_path=path}};
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        if(request.Content is MultipartFormDataContent form)
        {
            var upload=new Dictionary<string,string>();byte[] bytes=[];
            foreach(var part in form)
            {
                var name=part.Headers.ContentDisposition!.Name!.Trim('"');
                if(name=="file"){upload[name]=part.Headers.ContentDisposition.FileName!.Trim('"');bytes=await part.ReadAsByteArrayAsync(token);}
                else upload[name]=await part.ReadAsStringAsync(token);
            }
            Requests.Enqueue(upload);Uploads.Enqueue(upload);
            if(Intercept is {} onUpload&&await onUpload(request,upload,token) is {} uploadReply)return uploadReply;
            var path=upload["path"].TrimEnd('/')+"/"+upload["file"];
            if(Files.ContainsKey(path)&&!IgnoreCreateOnly)return SkipExisting?Success(new{}):Failure(1805);
            Files[path]=bytes;if(path==DropUpload)throw new HttpRequestException("simulated secret-containing lost reply");return Success(new{});
        }
        var fields=request.Method==HttpMethod.Get?Fields(request.RequestUri!.Query):Fields(await request.Content!.ReadAsStringAsync(token));Requests.Enqueue(fields);
        RawRequests.Enqueue(new(fields));
        var api=fields["api"];var method=fields["method"];
        if(JsonFormat&&api is not("SYNO.API.Info" or "SYNO.API.Auth"))foreach(var field in fields.ToArray())
            if(field.Value.StartsWith('"'))fields[field.Key]=JsonSerializer.Deserialize<string>(field.Value)!;
        if(Intercept is {} intercept&&await intercept(request,fields,token) is {} reply)return reply;
        if(api=="SYNO.API.Info")
        {
            Interlocked.Increment(ref Discoveries);if(DiscoveryStatus!=0)return new((HttpStatusCode)DiscoveryStatus);
            if(LegacyMissing&&request.RequestUri!.AbsolutePath.EndsWith("query.cgi",StringComparison.Ordinal))return new(HttpStatusCode.NotFound);
            var apis=new[]{"SYNO.API.Auth","SYNO.FileStation.List","SYNO.FileStation.Download","SYNO.FileStation.MD5","SYNO.FileStation.Upload","SYNO.FileStation.Delete","SYNO.FileStation.CreateFolder"};
            var result=apis.Where(x=>!OmitWrites||x is not("SYNO.FileStation.Upload" or "SYNO.FileStation.Delete" or "SYNO.FileStation.CreateFolder"))
                .ToDictionary(x=>x,x=>(object)new{path=x=="SYNO.API.Auth"&&AuthMax<6?"auth.cgi":"entry.cgi",minVersion=1,maxVersion=x=="SYNO.API.Auth"?AuthMax:17,requestFormat=JsonFormat?"JSON":""});
            result["UNRELATED.INVALID.API"]=new{path="../not-used",minVersion=0,maxVersion=0};return Success(result);
        }
        if(api=="SYNO.API.Auth")
        {
            if(method=="logout")return Success(new{});
            Logins.Enqueue(fields);if(RequireOtp&&!fields.ContainsKey("otp_code")&&!fields.ContainsKey("device_id"))return Failure(403);return LoginError!=0?Failure(LoginError):Success(new{sid="sid-"+Logins.Count,did="device-token",synotoken="csrf"});
        }
        if(api=="SYNO.FileStation.List"&&method=="getinfo")
        {
            if(Expire>0){Interlocked.Decrement(ref Expire);return Failure(SessionError);}if(MetadataError!=0)return Failure(MetadataError);
            var path=JsonSerializer.Deserialize<string[]>(fields["path"])![0];return path=="/share"||Files.ContainsKey(path)?Success(new{files=new[]{File(path)}}):Failure(408);
        }
        if(api=="SYNO.FileStation.List"&&method=="list")
        {
            var paths=Files.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();var offset=int.Parse(fields["offset"],System.Globalization.CultureInfo.InvariantCulture);
            return Success(new{total=paths.Length,files=paths.Skip(offset).Take(2).Select(File).ToArray()});
        }
        if(api=="SYNO.FileStation.List"&&method=="list_share")return Success(new{total=1,shares=new[]{new{name="share",path="/share",additional=new{volume_status=new{freespace=12,totalspace=32}}}}});
        if(api=="SYNO.FileStation.MD5")
        {
            if(method=="start"){var id=Guid.NewGuid().ToString("N");_md5Tasks[id]=fields["file_path"];HashStarted.TrySetResult();return Success(new{taskid=id});}
            if(method=="stop")return Success(new{});
            if(HoldMd5)return Success(new{finished=false});
            if(!Files.TryGetValue(_md5Tasks[fields["taskid"]],out var bytes))return Failure(408);
            return Success(new{finished=true,md5=Convert.ToHexString(MD5.HashData(bytes))});
        }
        if(api=="SYNO.FileStation.Delete")
        {
            if(method=="start"){Files.TryRemove(JsonSerializer.Deserialize<string[]>(fields["path"])![0],out _);return Success(new{taskid="delete-task"});}
            return Success(new{finished=true});
        }
        if(api=="SYNO.FileStation.Download")
        {
            Interlocked.Increment(ref Downloads);var path=JsonSerializer.Deserialize<string[]>(fields["path"])![0];var bytes=Files[path].ToArray();
            if(AfterDownload is {} change)Files[path]=change();return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
        }
        throw new InvalidOperationException("Unexpected fake API: "+api+"/"+method);
    }
}
