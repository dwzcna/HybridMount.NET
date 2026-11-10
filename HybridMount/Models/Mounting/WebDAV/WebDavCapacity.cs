using System.Net;
using HybridMount.Models.Mounting;
using WebDAVClient;
using WebDAVClient.Model;
using WebDAVClient.Protocol;

namespace HybridMount.Models.Mounting.WebDAV;

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
        catch(OperationCanceledException error)when(!token.IsCancellationRequested){throw WebDavNetworkDiagnostics.Failure(error,"PROPFIND 远端配额",timeout:true);}
        catch(HttpRequestException error){throw WebDavNetworkDiagnostics.Failure(error,"PROPFIND 远端配额");}
        catch(WebDAVClient.Helpers.WebDAVException){throw new IOException("WebDAV 配额 XML 无效。");}
    }

    /// <summary>手动上限统计配置根内的文件；包括实际存在的临时文件，只读 Depth 1，不下载正文。</summary>
    public async Task<RemoteUsage> GetRemoteUsageAsync(CancellationToken token=default)
    {
        var queue=new Queue<string>();queue.Enqueue("");var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase){""};long used=0;
        while(queue.Count>0)
        {
            token.ThrowIfCancellationRequested();var parent=queue.Dequeue();
            var entries=await PropFindAsync(parent,1,token).ConfigureAwait(false);
            if(entries.Count(x=>x.Path==parent&&x.IsDirectory)!=1)throw new IOException("容量统计目录响应不完整。");
            foreach(var entry in entries)
            {
                if(entry.Path==parent)continue;
                if(RelativePaths.Parent(entry.Path)!=parent||entry.Size<0||!seen.Add(entry.Path))throw new IOException("容量统计含越界、重复或无效条目。");
                if(seen.Count>1_000_000)throw new IOException("容量统计超过安全条目限制，未采用部分结果。");
                if(entry.IsDirectory)queue.Enqueue(entry.Path);
                else try{used=checked(used+entry.Size);}catch(OverflowException){throw new IOException("远端用量超出字节范围。");}
            }
        }
        token.ThrowIfCancellationRequested();return new("WebDAV 配置根内文件",used);
    }
}
