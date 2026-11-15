using HybridMount.Commons.Presentation;
using HybridMount.Models.Protocols.WebDAV;
namespace HybridMount.Models.Mounting;

/// <summary>可公开的连接失败分类，不含原始响应、凭据或授权头。</summary>
public enum ConnectionFailureKind { Authentication, Signature, Permission, NotFound, Network, Certificate, Service, Tls, Timeout }
/// <summary>单阶段安全计时，不含目标地址或凭据；只用于当前界面诊断。</summary>
public sealed record ConnectionTransportStage(string Stage,string Status,double Milliseconds,string? AddressFamily=null,int? HttpStatus=null,string? CancellationSource=null);
/// <summary>服务返回的有限结构化诊断；区域提示不自动修改用户配置。</summary>
public sealed record ConnectionDiagnostic(string Stage,ConnectionFailureKind Kind,int? HttpStatus,string? ServiceCode,
    string? RequestId,string? SuggestedRegion,string Explanation,string? TransportError=null,string? SocketError=null,
    int? NativeError=null,string? CertificateError=null,string? ExceptionType=null,int? SafeHResult=null,bool Transient=false,int Attempts=1,string? ExceptionChain=null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ConnectionTransportStage>? TransportStages {get;init;}
    public string Display => ModelPresentation.Render(this,nameof(Display));
}
/// <summary>只暴露经过筛选的诊断，不保留可能包含密钥或响应正文的 SDK 原始异常。</summary>
public sealed class SourceConnectionException(ConnectionDiagnostic diagnostic) : IOException(diagnostic.Display)
{
    public ConnectionDiagnostic Diagnostic { get; }=diagnostic;
    [System.Text.Json.Serialization.JsonIgnore]
    public HybridMount.Models.Protocols.WebDAV.WebDavCertificateInfo? Certificate { get; init; }
}
/// <summary>只读表单探测结果；成功不等于具备上传、删除或条件修改权限。</summary>
public sealed record ConnectionTestResult(bool Success,string Message,ConnectionDiagnostic? Diagnostic=null,[property:System.Text.Json.Serialization.JsonIgnore] ProtocolCapabilitySnapshot? Capabilities=null,
    [property:System.Text.Json.Serialization.JsonIgnore] HybridMount.Models.Protocols.WebDAV.WebDavCertificateInfo? Certificate=null);
