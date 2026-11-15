using HybridMount.Infrastructure.Persistence;
using HybridMount.Commons.Protocols;
using HybridMount.Infrastructure.Files.Content;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Models.Configuration;
using HybridMount.Models.Mounting;
namespace HybridMount.Infrastructure.Protocols.WebDAV;

/// <summary>DAV 的工厂策略；只在实际 Create 时建立客户端，表单超时使用安全协议诊断。</summary>
internal sealed class WebDavSourceProtocol : IFileSourceProtocol
{
    /// <summary>仅由协议备份入口读取当前用户DPAPI凭据，不向契约层泄漏存储实现。</summary>
    public SourceCredentials BackupCredentials(MountDefinition mount,string directory)=>new CredentialStore(directory).Load(mount.Id);

    public IFileSource Create(FileSourceConfiguration options,SourceCredentials credentials,string work)=>new WebDavFileSource(options,credentials,new WholeFileCache(work));
    public string CapacityWriteScope(FileSourceConfiguration options)=>"DAV:"+FileSourceFactory.Endpoint(options).GetLeftPart(UriPartial.Authority);
    public void ValidateOptions(FileSourceConfiguration options)
    {FileSourceFactory.Endpoint(options);WebDavFileSource.NormalizeCertificatePin(options);}
    public void ValidateBackupFields(MountBackupItem item)
    {if(item.Source.Kind==FileSourceKind.WebDav)ValidateOptions(item.Source);}
    public void ValidateCredentials(FileSourceConfiguration options,SourceCredentials credentials)
    {if(credentials.UserName.Contains(':'))throw new ArgumentException("WebDAV 账号不能包含冒号。");}
    public ConnectionTestResult Timeout(OperationCanceledException error)
    {var failure=WebDavNetworkDiagnostics.Failure(error,"测试连接",timeout:true);return new(false,failure.Message,failure.Diagnostic);}
}
