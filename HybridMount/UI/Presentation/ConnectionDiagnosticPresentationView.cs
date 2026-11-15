using HybridMount.Models.Protocols.WebDAV;
using HybridMount.Models.Mounting;
using HybridMount.UI.Localization;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class ConnectionDiagnosticPresentationView(ConnectionDiagnostic value)
{
    private readonly ConnectionDiagnostic _value=value;
    private string Stage => _value.Stage;
    private ConnectionFailureKind Kind => _value.Kind;
    private int? HttpStatus => _value.HttpStatus;
    private string? ServiceCode => _value.ServiceCode;
    private string? RequestId => _value.RequestId;
    private string? SuggestedRegion => _value.SuggestedRegion;
    private string Explanation => _value.Explanation;
    private string? TransportError => _value.TransportError;
    private string? SocketError => _value.SocketError;
    private int? NativeError => _value.NativeError;
    private string? CertificateError => _value.CertificateError;
    private string? ExceptionType => _value.ExceptionType;
    private int? SafeHResult => _value.SafeHResult;
    private bool Transient => _value.Transient;
    private int Attempts => _value.Attempts;
    private string? ExceptionChain => _value.ExceptionChain;
    private IReadOnlyList<ConnectionTransportStage>? TransportStages => _value.TransportStages;
public string Display => $"阶段：{Stage}；{Explanation}" +
        (HttpStatus is {} status?$"；HTTP {status}":"") + (ServiceCode is {} code?$"；服务码：{code}":"") +
        (RequestId is {} request?$"；请求 ID：{request}":"") + (SuggestedRegion is {} region?$"；服务建议区域：{region}（需确认）":"")+
        (TransportError is {} transport?$"；传输：{transport}":"")+(SocketError is {} socket?$"；Socket：{socket}":"")+
        (NativeError is {} native?$"；原生码 {native}（0x{native:X8}）":"")+(CertificateError is {} certificate?$"；证书：{certificate}":"")+
        (ExceptionType is {} type?$"；异常类型：{type}":"")+(SafeHResult is {} hr?$"；HRESULT 0x{hr:X8}":"")+(Attempts>1?$"；尝试 {Attempts} 次":"")+(ExceptionChain is {} chain?$"；异常链：{chain}":"")+
        (TransportStages is {Count:>0} stages?"\n"+string.Join("\n",stages.Select(item=>$"{item.Stage} {item.Status} {item.Milliseconds:F1} ms"+(item.AddressFamily is {} family?" / "+family:"")+(item.HttpStatus is {} code?" / HTTP "+code:"")+(item.CancellationSource is {} source?" / "+source:""))):"");
}
