using System.Net;
using HybridMount.Models.Mounting;
using WebDAVClient;
using WebDAVClient.Model;
using WebDAVClient.Protocol;

namespace HybridMount.Services.Mounting;

public sealed partial class WebDavFileSource
{
    private static readonly PropertyName QuotaAvailable=new("DAV:","quota-available-bytes"),QuotaUsed=new("DAV:","quota-used-bytes");
    /// <summary>Depth 0 只读 RFC 4331 配额；缺失值单独未知，不遍历对象或读取本地盘容量。</summary>
    public async Task<RemoteCapacity> GetRemoteCapacityAsync(CancellationToken token=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(MetadataDeadline);
        try
        {
            var result=await _dav.PropFindAsync(DavPath.Root,new PropFindOptions{Depth=DavDepth.Zero,
                Selection=DavPropertySelection.Selected,Properties=[QuotaAvailable,QuotaUsed]},deadline.Token).ConfigureAwait(false);
            if(result.StatusCode!=207)
            {using var response=new HttpResponseMessage((HttpStatusCode)result.StatusCode);Success(response);throw new IOException("配额查询未返回 DAV Multi-Status。");}
            if(result.Resources.Count!=1)throw new IOException("配额响应必须仅包含挂载根。");
            var resource=result.Resources[0];
            if(resource.Hrefs.Count!=1||HrefPath(resource.Hrefs[0],_endpoint)!=""||resource.StatusCode is >=400)
                throw new IOException("配额响应范围或资源状态无效。");
            long? Read(PropertyName property)
            {
                var values=resource.GetProperties(property).ToArray();if(values.Length!=1)return null;
                var value=values[0];if(value.StatusCode==404)return null;
                if(value.StatusCode is null)throw new IOException("远端配额属性缺少状态。");
                if(value.StatusCode is <200 or >=300)throw new SourceRequestRejectedException(value.StatusCode.Value,"远端配额属性读取失败。");
                var xml=value.Value.ToElement();return xml.HasElements?null:RemoteCapacity.ParseBytes(xml.Value);
            }
            return new("服务器报告的账户／目录配额（非物理磁盘容量）",Read(QuotaAvailable),Read(QuotaUsed));
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new IOException("WebDAV 配额查询超时。");}
        catch(HttpRequestException){throw new IOException("WebDAV 配额网络请求失败，请检查网络与 TLS 连接。");}
        catch(WebDAVClient.Helpers.WebDAVException){throw new IOException("WebDAV 配额 XML 无效。");}
    }
}
