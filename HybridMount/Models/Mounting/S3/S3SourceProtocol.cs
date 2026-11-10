using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.S3;

/// <summary>S3 的装配、配置与凭据校验；签名和网络实现均保留协议模块。</summary>
internal sealed partial class S3SourceProtocol : IFileSourceProtocol
{
    public IFileSource Create(FileSourceConfiguration options,SourceCredentials credentials,string work)=>new S3FileSource(options,credentials,work);
    public string CapacityWriteScope(FileSourceConfiguration options)=>"S3:"+FileSourceFactory.Endpoint(options).AbsoluteUri+"\0"+options.Bucket;
    public void ValidateOptions(FileSourceConfiguration options)=>S3FileSource.ValidateOptions(options);
    public void ValidateCredentials(FileSourceConfiguration options,SourceCredentials credentials)
    {if(!options.UseDefaultCredentials&&(string.IsNullOrWhiteSpace(credentials.AccessKey)||string.IsNullOrWhiteSpace(credentials.SecretKey)))throw new ArgumentException("请填写 S3 访问密钥或选择默认凭据链。");}
    public ConnectionTestResult Timeout(OperationCanceledException error)=>new(false,"连接测试超时，请检查网络及服务响应。");
}
