using System.Text.Json;
using Amazon.S3.Model;

namespace HybridMount.Services.Mounting;

public sealed partial class S3FileSource : IMountSourceRuntime
{
    private DateTimeOffset _lastRecoveryWarning=DateTimeOffset.MinValue;
    private void RecoveryWarning(int quarantined,int deferred)
    {
        var now=DateTimeOffset.UtcNow;if(now-_lastRecoveryWarning<TimeSpan.FromMinutes(1))return;_lastRecoveryWarning=now;
        Warn($"S3 分片恢复：已隔离 {quarantined} 条记录，{deferred} 条待重试；读取仍可用。");
    }
    private async Task RecoverPendingUploadAsync(UploadRecord record,CancellationToken token)
    {
        var start=record.StartedUtc!.Value.ToUniversalTime();var lower=start.AddSeconds(-30);var upper=start.AddMinutes(5);
        var uploads=new HashSet<string>(StringComparer.Ordinal);var cursors=new HashSet<(string?,string?)>();string? keyMarker=null,idMarker=null;
        do
        {
            var page=await Call(()=>_client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest{BucketName=_bucket,Prefix=record.Key,KeyMarker=keyMarker,UploadIdMarker=idMarker},token),token).ConfigureAwait(false);
            foreach(var upload in page.MultipartUploads??[])
            {
                if(upload.Key!=record.Key)continue;
                if(string.IsNullOrEmpty(upload.UploadId)||upload.Initiated is null)throw new IOException("S3 待恢复分片列表不完整。");
                var initiated=new DateTimeOffset(DateTime.SpecifyKind(upload.Initiated.Value,DateTimeKind.Utc));
                // S3 列表不回传客户端令牌；同一键在此时间窗内由其他客户端创建的上传仍可能被中止。
                if(initiated>=lower&&initiated<=upper)uploads.Add(upload.UploadId);
            }
            if(page.IsTruncated is null)throw new IOException("S3 待恢复分片分页不完整。");
            if(!page.IsTruncated.Value)break;
            keyMarker=page.NextKeyMarker;idMarker=page.NextUploadIdMarker;
            if(string.IsNullOrEmpty(keyMarker)||!cursors.Add((keyMarker,idMarker)))throw new IOException("S3 待恢复分片游标缺失或重复。");
        }while(true);
        foreach(var id in uploads){token.ThrowIfCancellationRequested();await AbortAsync(record with{UploadId=id},token).ConfigureAwait(false);}
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
    /// <summary>读取挂载建立后才恢复已记录的上传及待确认的初始化时间窗；失败不阻止读取。</summary>
    Task IMountSourceRuntime.AfterRegistrationAsync(CancellationToken token)=>RecoverKnownUploadsAsync(token);
    string IMountSourceRuntime.RecoveryWarning=>"已知分片恢复失败，读取仍可用；请稍后重试连接。";
    string IMountSourceRuntime.RecoveryLogArea=>"S3 分片恢复";
}
