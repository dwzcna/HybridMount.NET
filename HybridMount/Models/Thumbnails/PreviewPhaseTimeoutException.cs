namespace HybridMount.Models.Thumbnails;
/// <summary>有限内部阶段，不携带文件名、地址或版本；调用方取消继续使用原取消语义。</summary>
internal enum PreviewPhase {Prefetch,Startup,Read,Decode}
/// <summary>区别首块／启动／读取／解码自身期限，避免把P4排队时间误记为编解码器超时。</summary>
internal sealed class PreviewPhaseTimeoutException(PreviewPhase phase):OperationCanceledException("Preview phase deadline exceeded")
{
    internal PreviewPhase Phase {get;}=phase;
    internal string Stage=>Phase switch{PreviewPhase.Prefetch=>"首块预取期限已到",PreviewPhase.Startup=>"工作进程启动期限已到",PreviewPhase.Read=>"工作读取期限已到",_=>"解码期限已到"};
}
