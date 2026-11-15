namespace HybridMount.Models.Mounting;

/// <summary>有效权限独立于内容版本；缺失证据不等于拒绝，权限不能证明条件写或原子替换可靠。</summary>
public enum SourceAccessDecision { Unknown,Allowed,Denied }
public enum SourcePrivilege { Read,WriteContent,Bind,Unbind }
/// <summary>精确路径和连接的只读权限快照；仅当前连接内60秒可复用，持久展示不可作为下一次写入授权。</summary>
public sealed record SourceAccessSnapshot(Guid SessionId,string Path,DateTimeOffset ObservedUtc,
    SourceAccessDecision Read,SourceAccessDecision WriteContent,SourceAccessDecision Bind,SourceAccessDecision Unbind,string Reason)
{
    public SourceAccessDecision For(SourcePrivilege privilege)=>privilege switch
    {SourcePrivilege.Read=>Read,SourcePrivilege.WriteContent=>WriteContent,SourcePrivilege.Bind=>Bind,SourcePrivilege.Unbind=>Unbind,_=>SourceAccessDecision.Unknown};
}
/// <summary>本地意图已持久化后，在任何目标写入前返回；失败只阻止相关路径，原正文／快照／基线保留。</summary>
public sealed class SourcePermissionException(SourceAccessSnapshot access,SourcePrivilege privilege):IOException(
    string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.Services.Localization.UILanguageService.Messages.GetString(access.For(privilege)==SourceAccessDecision.Denied?"SourcePermissionBlocked":"SourcePermissionPending", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!, access.Path, privilege.ToString()))
{
    public SourceAccessSnapshot Access {get;}=access;
    public bool Pending=>Access.For(privilege)==SourceAccessDecision.Unknown;
}
/// <summary>缓存失效是纯内存操作；手工重试和重连必须重新核验，读取展示不发网络请求。</summary>
public interface IFileSourceAccess
{
    SourceAccessSnapshot? AccessAt(string path);
    void InvalidateAccess();
}
