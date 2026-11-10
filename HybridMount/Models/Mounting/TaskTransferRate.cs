using System.Diagnostics;
namespace HybridMount.Models.Mounting;

/// <summary>任务显示严重程度；普通任务绿色，问题红色，冲突黄色。</summary>
public enum TaskIssueKind { Normal, Error, Conflict }
/// <summary>页面内的有界速率采样器；只使用真实累计字节和单调时间，不读取文件或访问网络。</summary>
internal sealed class TaskTransferRate
{
    private sealed record Sample(long Bytes,long At,long Advanced,double Rate,string? Path);
    private readonly Dictionary<string,Sample> _samples=new(StringComparer.Ordinal);
    internal MountOperationSnapshot Apply(MountOperationSnapshot value,long now)
    {
        if(value.Operation is not("upload" or "download" or "keepOnDevice")||value.State is not("Sending" or "Downloading" or "Running")||value.IsDirectory is true||value.WaitingReason is not null)
        {_samples.Remove(value.Id);return value with{BytesPerSecond=0};}
        var path=value.RelativeItemPath??value.ItemPath;
        if(!_samples.TryGetValue(value.Id,out var old)||old.Path!=path||value.BytesTransferred<old.Bytes)
        {
            if(_samples.Count>=2048)_samples.Remove(_samples.MinBy(pair=>pair.Value.At).Key);
            _samples[value.Id]=new(value.BytesTransferred,now,now,0,path);return value with{BytesPerSecond=0};
        }
        var elapsed=Stopwatch.GetElapsedTime(old.At,now).TotalSeconds;var advanced=value.BytesTransferred>old.Bytes;
        var rate=Stopwatch.GetElapsedTime(old.Advanced,now).TotalSeconds>=2?0:old.Rate;
        if(advanced&&elapsed>=0.25){rate=(value.BytesTransferred-old.Bytes)/elapsed;old=new(value.BytesTransferred,now,now,rate,path);_samples[value.Id]=old;}
        return value with{BytesPerSecond=rate};
    }
    /// <summary>每次快照移除已消失任务，避免页面缓存保留无限历史样本。</summary>
    internal void Retain(IReadOnlySet<string> ids){foreach(var id in _samples.Keys.Where(id=>!ids.Contains(id)).ToArray())_samples.Remove(id);}
}
