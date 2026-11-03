namespace HybridMount.Services.Mounting;

/// <summary>S3 配置迁移策略；保留 v3 字段和独立队列 DPAPI 路径，不建立 SDK 客户端或解析默认凭据链。</summary>
internal sealed partial class S3SourceProtocol
{
    private static CredentialStore QueueStore(string directory)=>new(Path.Combine(directory,"Sqs"));
    public SourceCredentials BackupCredentials(MountDefinition mount,string directory)=>mount.Source!.UseDefaultCredentials?new():new CredentialStore(directory).Load(mount.Id);
    public SourceCredentials NormalizeImportCredentials(FileSourceConfiguration source,SourceCredentials credentials)=>source.UseDefaultCredentials?new():credentials;
    public SourceCredentials? BackupAdditionalCredentials(MountDefinition mount,string directory)=>mount.Sqs?.CredentialMode==SqsCredentialMode.SeparateKeys?QueueStore(directory).Load(mount.Id):null;
    public void SaveAdditionalCredentials(MountDefinition mount,SourceCredentials? credentials,string directory)
    {if(mount.Sqs?.CredentialMode==SqsCredentialMode.SeparateKeys&&credentials is not null)QueueStore(directory).Save(mount.Id,credentials);}
    public void DeleteAdditionalCredentials(Guid mount,string directory)=>QueueStore(directory).Delete(mount);
    public void ValidateBackupFields(MountBackupItem item)
    {
        if(item.Sqs is {} queue&&(item.Source.Kind!=FileSourceKind.S3||!Enum.IsDefined(queue.CredentialMode)||queue.QueueUrl is null||queue.Region is null||queue.QueueUrl.Length>2048||queue.Region.Length>64))throw new InvalidDataException("备份包含无效 SQS 配置。");
        if(item.SqsCredentials is {} keys&&(keys.AccessKey is null||keys.SecretKey is null||keys.SessionToken is null))throw new InvalidDataException("备份队列凭据不完整。");
    }
}
