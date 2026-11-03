namespace HybridMount.Services.Mounting;

public sealed partial class S3FileSource : IMountSourceRuntime
{
    /// <summary>读取挂载建立后才恢复本程序记录的 UploadId；失败继续允许读取，不枚举外部上传。</summary>
    Task IMountSourceRuntime.AfterRegistrationAsync(CancellationToken token)=>RecoverKnownUploadsAsync(token);
    string IMountSourceRuntime.RecoveryWarning=>"已知分片恢复失败，读取仍可用；请稍后重试连接。";
    string IMountSourceRuntime.RecoveryLogArea=>"S3 分片恢复";
}
