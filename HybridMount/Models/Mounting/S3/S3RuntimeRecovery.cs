using System.Text.Json;

namespace HybridMount.Models.Mounting.S3;

public sealed partial class S3FileSource : IMountSourceRuntime, IMountNetworkSource
{
    private MountQueueBinding? _queueBinding;
    MountQueueBinding? IMountNetworkSource.QueueBinding=>_queueBinding;
    void IMountNetworkSource.BindQueue(MountQueueBinding binding)=>_queueBinding=binding;
    private DateTimeOffset _lastRecoveryWarning=DateTimeOffset.MinValue;
    private void RecoveryWarning(int quarantined,int deferred)
    {
        var now=DateTimeOffset.UtcNow;if(now-_lastRecoveryWarning<TimeSpan.FromMinutes(1))return;_lastRecoveryWarning=now;
        Warn($"S3 分片恢复：已隔离 {quarantined} 条记录，{deferred} 条待重试；读取仍可用。");
    }
    /// <summary>标准分片列表不回传客户端令牌；缺少UploadId不能证明归属，保留意图而不猜测或中止外部上传。</summary>
    private static Task RecoverPendingUploadAsync(UploadRecord record,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        throw new IOException("分片初始化结果尚待核对，缺少已记录UploadId；保留意图，未自动枚举或中止上传。");
    }
    /// <summary>隔离损坏记录；未确认的网络或权限失败留待下次恢复，连接探测不调用此方法。</summary>
    public async Task RecoverKnownUploadsAsync(CancellationToken token=default)
    {
        if(!Directory.Exists(_work))return;var quarantined=0;var deferred=0;string[] paths;
        try{paths=Directory.GetFiles(_work,"*.multipart.json");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){RecoveryWarning(0,1);return;}
        foreach(var path in paths)
        {
            token.ThrowIfCancellationRequested();UploadRecord record;
            try
            {
                record=JsonSerializer.Deserialize<UploadRecord>(await File.ReadAllTextAsync(path,token).ConfigureAwait(false))??throw new InvalidDataException();
                if(record.Bucket!=_bucket||string.IsNullOrEmpty(record.Key)||!record.Key.StartsWith(_prefix,StringComparison.Ordinal)||record.Key.Length<=_prefix.Length||
                    string.IsNullOrEmpty(record.UploadId)&&(!Guid.TryParseExact(record.ClientToken,"N",out _)||record.StartedUtc is null||record.StartedUtc<=DateTimeOffset.MinValue.AddSeconds(30)||record.StartedUtc>=DateTimeOffset.MaxValue.AddMinutes(-5)))throw new InvalidDataException();
            }
            catch(Exception error)when(error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                try{File.Move(path,path+".bad"+(File.Exists(path+".bad")?"."+Guid.NewGuid().ToString("N"):""));quarantined++;}
                catch(Exception move)when(move is IOException or UnauthorizedAccessException){deferred++;}
                continue;
            }
            try
            {
                if(string.IsNullOrEmpty(record.UploadId))await RecoverPendingUploadAsync(record,token).ConfigureAwait(false);else await AbortAsync(record,token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();File.Delete(path);
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or OperationCanceledException)
            {token.ThrowIfCancellationRequested();deferred++;}
        }
        if(quarantined+deferred>0)RecoveryWarning(quarantined,deferred);
    }
    /// <summary>读取挂载建立后只恢复精确记录的UploadId；无ID意图延后，失败不阻止读取。</summary>
    Task IMountSourceRuntime.AfterRegistrationAsync(CancellationToken token)=>RecoverKnownUploadsAsync(token);
    string IMountSourceRuntime.RecoveryWarning=>"已知分片恢复失败，读取仍可用；请稍后重试连接。";
    string IMountSourceRuntime.RecoveryLogArea=>"S3 分片恢复";
}
