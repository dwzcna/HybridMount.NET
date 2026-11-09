namespace HybridMount.Models.Mounting;

/// <summary>单个会话的同类网络错误五分钟合并；真实故障仍保留首条、窗口汇总与恢复记录。</summary>
internal sealed class NetworkFailureLog(Func<DateTimeOffset>? clock=null)
{
    private readonly object _sync=new();
    private string? _key;
    private DateTimeOffset _logged;
    private int _suppressed;
    public bool ShouldWrite(ConnectionDiagnostic diagnostic,out string suffix)
    {
        lock(_sync)
        {
            var now=clock?.Invoke()??DateTimeOffset.UtcNow;
            var key=string.Join('|',diagnostic.Stage,diagnostic.Kind,diagnostic.TransportError,diagnostic.SocketError,diagnostic.CertificateError);
            suffix="";
            if(_key==key&&now-_logged<TimeSpan.FromMinutes(5)){_suppressed++;return false;}
            if(_suppressed>0)suffix=$"（此前重复失败已合并 {_suppressed} 次）";
            _key=key;_logged=now;_suppressed=0;return true;
        }
    }
    /// <summary>成功只清除本会话的错误合并窗口；返回恢复说明供所有者记录。</summary>
    public bool Recovered(out string message)
    {
        lock(_sync)
        {
            message=_suppressed>0?$"网络读取已恢复，期间合并 {_suppressed} 次重复失败。":"网络读取已恢复。";
            if(_key is null)return false;_key=null;_suppressed=0;return true;
        }
    }
}
