using System.Text;

namespace HybridMount.Infrastructure.Protocols.WebDAV;

/// <summary>所有重定向只在同源及配置根内；URI比较不依赖显示名称，不携带查询、认证材料或编码逃逸。</summary>
internal static class WebDavRedirectPolicy
{
    internal static bool SameOrigin(Uri first,Uri second)=>first.Scheme.Equals(second.Scheme,StringComparison.OrdinalIgnoreCase)&&
        first.IdnHost.Equals(second.IdnHost,StringComparison.OrdinalIgnoreCase)&&first.Port==second.Port;
    internal static void ValidateTarget(Uri root,Uri target)
    {
        if(!target.IsAbsoluteUri||target.AbsoluteUri.Length>16384||!SameOrigin(root,target)||target.UserInfo.Length!=0||target.Query.Length!=0||target.Fragment.Length!=0)
            throw Rejected();
        var prefix=root.AbsolutePath.TrimEnd('/');var path=target.AbsolutePath;
        if(path!=prefix&&!path.StartsWith(prefix+"/",StringComparison.Ordinal))throw Rejected();
        foreach(var part in path.Split('/'))
        {
            var decoded=Uri.UnescapeDataString(part);
            for(var round=0;round<8;round++)
            {
                if(decoded is "." or ".."||decoded.Contains('/')||decoded.Contains('\\')||decoded.Any(char.IsControl))throw Rejected();
                var next=Uri.UnescapeDataString(decoded);if(next==decoded)break;if(round==7)throw Rejected();decoded=next;
            }
        }
    }
    internal static Uri Follow(Uri root,Uri current,IEnumerable<string> locations,ISet<string> visited,int hops)
    {
        var fields=locations.ToArray();if(hops>=5||fields.Length!=1||fields[0].Length is 0 or >8192||fields[0].Any(char.IsControl)||fields[0].Contains('\\'))throw Rejected();
        var raw=fields[0];ValidateReference(raw);
        if(!Uri.TryCreate(current,raw,out var target))throw Rejected();ValidateTarget(root,target);
        if(!visited.Add(target.AbsoluteUri))throw Rejected();return target;
    }
    internal static void ValidateReference(string raw)
    {
        if(raw.Length>16384||raw.Any(char.IsControl)||raw.Contains('\\'))throw Rejected();
        foreach(var part in raw.Split('/'))
            if(part.Contains('%')&&Uri.UnescapeDataString(part) is "." or "..")throw Rejected();
    }
    /// <summary>只重绑已明确映射的源资源标签；目标条件独立保留，其他资源标签拒绝猜测。</summary>
    internal static string RebindIf(string value,Uri previous,Uri target,Uri? destination)
    {
        var result=new StringBuilder(value.Length);var depth=0;
        for(var index=0;index<value.Length;index++)
        {
            var c=value[index];
            if(c=='(')depth++;else if(c==')'&&--depth<0)throw Rejected();
            if(c!='<'){result.Append(c);continue;}
            var end=value.IndexOf('>',index+1);if(end<0)throw Rejected();var item=value[(index+1)..end];
            if(depth==0)
            {
                if(!Uri.TryCreate(item,UriKind.Absolute,out var resource))throw Rejected();
                if(resource==previous)item=target.AbsoluteUri;else if(destination is null||resource!=destination)throw Rejected();
            }
            result.Append('<').Append(item).Append('>');index=end;
        }
        if(depth!=0)throw Rejected();return result.ToString();
    }
    internal static WebDavRedirectException Rejected()=>new("WebDAV重定向不满足同源、根目录、次数或明确条件映射要求，已停止跟随。");
}
/// <summary>确定的策略拒绝不作为瞬时网络错误重试；已发修改的业务所有者仍保留结果核对责任。</summary>
internal sealed class WebDavRedirectException(string message):IOException(message);
