namespace HybridMount.Models.Mounting;

/// <summary>可公开的连接失败分类，不含原始响应、凭据或授权头。</summary>
public enum ConnectionFailureKind { Authentication, Signature, Permission, NotFound, Network, Certificate, Service, Tls, Timeout }
/// <summary>服务返回的有限结构化诊断；区域提示不自动修改用户配置。</summary>
public sealed record ConnectionDiagnostic(string Stage,ConnectionFailureKind Kind,int? HttpStatus,string? ServiceCode,
    string? RequestId,string? SuggestedRegion,string Explanation,string? TransportError=null,string? SocketError=null,
    int? NativeError=null,string? CertificateError=null,string? ExceptionType=null,int? SafeHResult=null,bool Transient=false,int Attempts=1,string? ExceptionChain=null)
{
    public string Display => $"阶段：{Stage}；{Explanation}" +
        (HttpStatus is {} status?$"；HTTP {status}":"") + (ServiceCode is {} code?$"；服务码：{code}":"") +
        (RequestId is {} request?$"；请求 ID：{request}":"") + (SuggestedRegion is {} region?$"；服务建议区域：{region}（需确认）":"")+
        (TransportError is {} transport?$"；传输：{transport}":"")+(SocketError is {} socket?$"；Socket：{socket}":"")+
        (NativeError is {} native?$"；原生码 {native}（0x{native:X8}）":"")+(CertificateError is {} certificate?$"；证书：{certificate}":"")+
        (ExceptionType is {} type?$"；异常类型：{type}":"")+(SafeHResult is {} hr?$"；HRESULT 0x{hr:X8}":"")+(Attempts>1?$"；尝试 {Attempts} 次":"")+(ExceptionChain is {} chain?$"；异常链：{chain}":"");
}
/// <summary>只暴露经过筛选的诊断，不保留可能包含密钥或响应正文的 SDK 原始异常。</summary>
public sealed class SourceConnectionException(ConnectionDiagnostic diagnostic) : IOException(diagnostic.Display)
{
    public ConnectionDiagnostic Diagnostic { get; }=diagnostic;
}
/// <summary>只读表单探测结果；成功不等于具备上传、删除或条件修改权限。</summary>
public sealed record ConnectionTestResult(bool Success,string Message,ConnectionDiagnostic? Diagnostic=null,[property:System.Text.Json.Serialization.JsonIgnore] ProtocolCapabilitySnapshot? Capabilities=null);
