namespace HybridMount.Commons.Diagnostics;

/// <summary>后台组件的非阻塞诊断入口；实现负责有界入队、去重和安全异常摘要。</summary>
public interface ILogWriter
{
    /// <summary>提交固定来源、级别和正文；不等待持久化，禁止传入认证材料。</summary>
    void Queue(string source, string severity, string message);
    /// <summary>记录一次安全异常摘要；关联标识不得包含路径或秘密。</summary>
    void QueueException(string source, string severity, string context, Exception error, string? correlation = null);
}
