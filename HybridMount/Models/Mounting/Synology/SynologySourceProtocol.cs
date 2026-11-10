namespace HybridMount.Models.Mounting.Synology;

public sealed record SynologySharedFolder(string Name,string Path)
{
    public override string ToString()=>Name;
}
public sealed record SynologyProbeResult(bool Success,string Message,IReadOnlyList<SynologySharedFolder> Shares,
    SourceCredentials? Credentials=null,string? CertificateSha256=null,int? AuthenticationCode=null)
{
    public override string ToString()=>"群晖只读连接测试结果";
}

internal sealed class SynologySourceProtocol : IFileSourceProtocol
{
    public IFileSource Create(FileSourceConfiguration options,SourceCredentials credentials,string work)=>new SynologyFileSource(options,credentials,new WholeFileCache(work));
    public string CapacityWriteScope(FileSourceConfiguration options)=>"Synology:"+FileSourceFactory.Endpoint(options).AbsoluteUri+"\0"+options.Prefix;
    public void ValidateOptions(FileSourceConfiguration options)=>SynologyFileSource.ValidateOptions(options);
    public void ValidateCredentials(FileSourceConfiguration options,SourceCredentials credentials)
    {if(string.IsNullOrWhiteSpace(credentials.UserName)||string.IsNullOrEmpty(credentials.Password))throw new ArgumentException("请填写群晖账号和密码。");}
    public ConnectionTestResult Timeout(OperationCanceledException error)=>new(false,"群晖连接测试超时，请检查 DSM 地址、端口与 File Station 服务。");
    public void ValidateBackupFields(MountBackupItem item)
    {if(item.Source.Kind==FileSourceKind.Synology)ValidateOptions(item.Source);}
}

public sealed class SynologyAuthenticationException(int code) : IOException($"群晖需要重新认证（代码 {code}）；请检查账号、权限或重新输入二步验证码。")
{
    public int Code {get;}=code;
}

public sealed class SynologyCertificateException(string fingerprint) : IOException("群晖 TLS 证书未通过验证；请核对服务器证书后再决定是否固定指纹。")
{
    public string CertificateSha256 {get;}=fingerprint;
}

internal sealed class SynologyApiException(int code) : IOException($"群晖 File Station 请求被拒绝（代码 {code}）。")
{
    internal int Code {get;}=code;
}
