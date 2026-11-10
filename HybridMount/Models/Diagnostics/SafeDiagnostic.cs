using System.ComponentModel;
using System.Net.Http;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Diagnostics;

/// <summary>异常日志边界；只选取结构化类型和代码，绝不读取Message、Data、URL或原始响应。</summary>
public static class SafeDiagnostic
{
    /// <summary>仅允许有限标识符；不使用正则替换任意消息冒充安全脱敏。</summary>
    public static string Field(string? value)=>value is {Length:>0 and <=128}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '_' or '-' or ':')?value:"unknown";
    /// <summary>最多八层异常链；取消由服务所有者决定是否应记录，方法不改变异常或事务。</summary>
    public static string ExceptionSummary(Exception error)
    {
        var parts=new List<string>();
        for(var current=error;current is not null&&parts.Count<8;current=current.InnerException)
        {
            var text=$"{Field(current.GetType().Name)}；HRESULT=0x{current.HResult:X8}";
            if(current is Win32Exception win32)text+=$"；Win32={win32.NativeErrorCode}";
            if(current is HttpRequestException {StatusCode:{} http})text+=$"；HTTP={(int)http}";
            if(current is SourceRequestRejectedException rejected)text+=$"；HTTP={rejected.HttpStatus}"+(rejected.HttpStatus switch{401=>"（认证失败）",403=>"（权限不足）",409 or 412=>"（远端条件冲突）",423=>"（资源锁定）",429=>"（请求限流）",507=>"（存储不足）",_=>""});
            if(current is SourceConflictException)text+="；文件版本、身份或同步基线不满足保护条件，请重新核对";
            if(current is SourceOutcomeUnknownException)text+="；写入结果未知，应先只读核对";
            if(current is SourceConnectionException connection)
            {
                var d=connection.Diagnostic;
                text+=$"；分类={d.Kind}；阶段={Field(d.Stage)}"+(d.HttpStatus is {} status?$"；HTTP={status}":"");
                text+=$"；服务码={Field(d.ServiceCode)}；传输={Field(d.TransportError)}；Socket={Field(d.SocketError)}";
                text+=$"；原始异常类型={Field(d.ExceptionType)}；证书={Field(d.CertificateError)}";
                if(d.SafeHResult is {} original)text+=$"；原始HRESULT=0x{original:X8}";
                if(d.NativeError is {} native)text+=$"；原生码={native}";
            }
            parts.Add(text);
        }
        return string.Join(" <- ",parts);
    }
}
