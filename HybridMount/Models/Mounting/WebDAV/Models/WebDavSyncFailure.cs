namespace HybridMount.Models.Mounting;

/// <summary>协议失败保留结构化分类；不含响应原文、认证信息或不透明同步令牌。</summary>
public sealed class RemoteSyncException(int status,bool invalidToken=false,TimeSpan? retryAfter=null,bool unsupportedReport=false)
    : RemoteChangeFailure(invalidToken?"WebDAV 同步令牌已失效，需要重新建立完整基线。":$"WebDAV 远端检测失败（HTTP {status}）。",invalidToken,unsupportedReport||status is 405 or 501,status is 401 or 403,retryAfter)
{
    public int Status {get;}=status;
    /// <summary>保留原公开属性名，通用协调器只消费基类的失效检查点能力。</summary>
    public bool InvalidToken=>InvalidCheckpoint;
}
