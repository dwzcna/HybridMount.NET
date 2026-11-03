namespace HybridMount.Models;

/// <summary>应用内部固定路由，无外部地址或参数；严格匹配原始 URI，避免别名和隐式参数。</summary>
public static class ApplicationRoutes
{
    public static Uri Home {get;}=new("hybridmount:///home");
    public static Uri Mounts {get;}=new("hybridmount:///mounts");
    public static Uri Tasks {get;}=new("hybridmount:///tasks");
    public static Uri Logs {get;}=new("hybridmount:///logs");
    public static Uri Settings {get;}=new("hybridmount:///settings");
    public static IReadOnlyList<Uri> All {get;}=Array.AsReadOnly(new[]{Home,Mounts,Tasks,Logs,Settings});
    public static bool IsRegistered(Uri? target)=>target is {IsAbsoluteUri:true}&&All.Any(route=>StringComparer.Ordinal.Equals(route.OriginalString,target.OriginalString));
}
