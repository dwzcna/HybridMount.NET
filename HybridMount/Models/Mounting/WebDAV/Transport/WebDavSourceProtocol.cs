using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>DAV 的工厂策略；只在实际 Create 时建立客户端，表单超时使用安全协议诊断。</summary>
internal sealed class WebDavSourceProtocol : IFileSourceProtocol
{
    public IFileSource Create(FileSourceConfiguration options,SourceCredentials credentials,string work)=>new WebDavFileSource(options,credentials,new WholeFileCache(work));
    public string CapacityWriteScope(FileSourceConfiguration options)=>"DAV:"+FileSourceFactory.Endpoint(options).GetLeftPart(UriPartial.Authority);
    public void ValidateOptions(FileSourceConfiguration options)=>FileSourceFactory.Endpoint(options);
    public void ValidateCredentials(FileSourceConfiguration options,SourceCredentials credentials)
    {if(credentials.UserName.Contains(':'))throw new ArgumentException("WebDAV 账号不能包含冒号。");}
    public ConnectionTestResult Timeout(OperationCanceledException error)
    {var failure=WebDavNetworkDiagnostics.Failure(error,"测试连接",timeout:true);return new(false,failure.Message,failure.Diagnostic);}
}
