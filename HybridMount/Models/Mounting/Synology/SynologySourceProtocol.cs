namespace HybridMount.Models.Mounting.Synology;

/// <summary>来自只读共享列表的可选远端根。</summary>
public sealed record SynologySharedFolder(string Name,string Path)
{
    public override string ToString()=>Name;
}
/// <summary>群晖配置、迁移和备份策略；设备令牌仅属于当前设备，不导出也不导入。</summary>
internal sealed class SynologySourceProtocol : IFileSourceProtocol
{
    public IFileSource Create(FileSourceConfiguration options,SourceCredentials credentials,string work)=>new SynologyFileSource(options,credentials,new WholeFileCache(work));
    public string CapacityWriteScope(FileSourceConfiguration options)=>"Synology:"+FileSourceFactory.Endpoint(options).AbsoluteUri+"\0"+options.Prefix;
    public void ValidateOptions(FileSourceConfiguration options)=>SynologyFileSource.ValidateOptions(options);
    public void ValidateCredentials(FileSourceConfiguration options,SourceCredentials credentials)
    {if(string.IsNullOrWhiteSpace(credentials.UserName)||string.IsNullOrEmpty(credentials.Password))throw new ArgumentException("请填写群晖账号和密码。");}
    public ConnectionTestResult Timeout(OperationCanceledException error)=>new(false,"群晖连接测试超时，请检查 DSM 地址、端口与 File Station 服务。");
    public SourceCredentials BackupCredentials(MountDefinition mount,string directory)=>new CredentialStore(directory).Load(mount.Id) with{SessionToken=""};
    public SourceCredentials NormalizeImportCredentials(FileSourceConfiguration options,SourceCredentials credentials)=>credentials with{SessionToken=""};
    public void ValidateBackupFields(MountBackupItem item)
    {if(item.Source.Kind==FileSourceKind.Synology)ValidateOptions(item.Source);else if(item.Source.PinnedCertificateSha256 is not null)throw new ArgumentException("证书指纹字段仅适用于群晖连接。");}
}
