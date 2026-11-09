using WebDAVClient;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.WebDAV;

public sealed partial class WebDavFileSource
{
    internal WebDavCapabilitySnapshot? CapabilitySnapshot {get;private set;}
    /// <summary>只捕获成功 OPTIONS 的有界头值；不新增请求，也不将通告升级为写入支持。</summary>
    private void CaptureOptions(HttpResponseMessage response)
    {
        if(!response.IsSuccessStatusCode)return;
        string? HeaderValue(string name)=>response.Headers.TryGetValues(name,out var values)||response.Content.Headers.TryGetValues(name,out values)?string.Join(", ",values):null;
        static string Safe(string? value)=>new((value??"").Where(c=>!char.IsControl(c)).Take(4096).ToArray());
        var dav=Safe(HeaderValue("DAV"));
        CapabilitySnapshot=new(DateTimeOffset.UtcNow,Safe(HeaderValue("Allow")),Safe(HeaderValue("Accept-Patch")),
            dav.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains("sabredav-partialupdate",StringComparer.OrdinalIgnoreCase));
    }
    private event Action? _capacityInvalidated;
    /// <summary>只使用 DAV 标准错误名判断明确的 507；损坏或无细分正文采用通用存储不足，不包含原始报文。</summary>
    private SourceRequestRejectedException StorageRejected(DavResponse? response=null)
    {
        var reasons=response?.Errors.Select(x=>x.ToElement()).SelectMany(x=>x.Name==Dav+"error"?x.Elements():new[]{x}).Select(x=>x.Name).ToHashSet()??[];
        var reason=reasons.Contains(Dav+"quota-not-exceeded")?"账户配额不足":reasons.Contains(Dav+"sufficient-disk-space")?"服务器磁盘不足":"存储不足";
        if(_capacityInvalidated is {} observers)foreach(Action observer in observers.GetInvocationList())try{observer();}catch{ /* 缓存通知失败不能改写服务已拒绝的结果。 */ }
        return new(507,$"WebDAV {reason}（HTTP 507），请释放空间后显式重试。");
    }
}
