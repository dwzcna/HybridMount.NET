using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>工厂装配的协议策略；参数校验和诊断由模块提供，不在公共协调器处理协议条件。</summary>
internal interface IFileSourceProtocol
{
    IFileSource Create(FileSourceConfiguration options,SourceCredentials credentials,string work);
    void ValidateOptions(FileSourceConfiguration options);
    void ValidateCredentials(FileSourceConfiguration options,SourceCredentials credentials);
    ConnectionTestResult Timeout(OperationCanceledException error);
    /// <summary>配置迁移只读取当前用户凭据；默认凭据链等协议模式由模块裁决，不解析环境秘密。</summary>
    SourceCredentials BackupCredentials(MountDefinition mount,string directory)=>new CredentialStore(directory).Load(mount.Id);
    SourceCredentials NormalizeImportCredentials(FileSourceConfiguration options,SourceCredentials credentials)=>credentials;
    SourceCredentials? BackupAdditionalCredentials(MountDefinition mount,string directory)=>null;
    void SaveAdditionalCredentials(MountDefinition mount,SourceCredentials? credentials,string directory) { }
    void DeleteAdditionalCredentials(Guid mount,string directory) { }
    void ValidateBackupFields(MountBackupItem item) { }
}
/// <summary>真实挂载的会话上下文；测试连接不绑定仓库，恢复分阶段且不得阻断已有读取权限。</summary>
internal interface IMountSourceRuntime
{
    string RemoteScope(long credentialRevision);
    void AttachRuntime(MountDefinition mount,MountRepository repository) { }
    Task BeforeRegistrationAsync(CancellationToken token)=>Task.CompletedTask;
    Task AfterRegistrationAsync(CancellationToken token)=>Task.CompletedTask;
    string RecoveryWarning=>"协议恢复未完成，读取仍可用；请稍后重新连接。";
    string RecoveryLogArea=>"协议恢复";
}
