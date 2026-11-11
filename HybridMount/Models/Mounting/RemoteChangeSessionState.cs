namespace HybridMount.Models.Mounting;

/// <summary>已成功读取的远端变更响应不满足结构约束；不用于网络、HTTP 拒绝或取消。</summary>
public sealed class InvalidRemoteChangeResponseException(string message,Exception? innerException=null):IOException(message,innerException);

/// <summary>当前挂载会话的降级证据；只驻留内存，不包含服务器令牌。</summary>
internal sealed record RemoteChangeFallback(string Reason,DateTimeOffset OccurredUtc);

/// <summary>挂载拥有的能力选择和降级状态；内部调度器重建复用，显式重连或重开检测换新实例。</summary>
internal sealed class RemoteChangeSessionState
{
    internal const string FallbackMode="完整对账（RFC 6578 响应异常，本次已停用）";
    private readonly object _sync=new();
    private readonly Dictionary<string,string> _modes=new(StringComparer.Ordinal);
    private RemoteChangeFallback? _fallback;
    private bool _fullScanRequired;
    public bool FullScanRequired {get{lock(_sync)return _fullScanRequired;}}
    public RemoteChangeFallback? Fallback {get{lock(_sync)return _fallback;}}
    public bool TryGetMode(string path,out string mode){lock(_sync)return _modes.TryGetValue(path,out mode!);}
    public void SetMode(string path,string mode){lock(_sync)_modes[path]=mode;}
    /// <summary>首次无效响应固定原因和时间；后续完整抓取失败不覆盖来源。</summary>
    public void Disable(InvalidRemoteChangeResponseException error){lock(_sync){_fallback??=new(error.Message,DateTimeOffset.UtcNow);_fullScanRequired=true;}}
    /// <summary>失败后的下一次调度必须重做全根扫描，不能用降级前的 Full 时间跳过。</summary>
    public void RequireFullScan(){lock(_sync)_fullScanRequired=true;}
    public void CompleteFullScan(){lock(_sync)_fullScanRequired=false;}
}
