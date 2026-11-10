using System.Diagnostics;
using HybridMount.Models.Mounting;
namespace HybridMount.Services;

public sealed partial class TasksQueueService
{
    private readonly HashSet<DownloadProgress> _downloadProgress=[];
    /// <summary>每个正在读取的请求拥有展示租约；只统计已经交付的真实字节，不增加正文读取。</summary>
    internal sealed class DownloadProgress(TasksQueueService owner,string id,string path,long total):IDisposable
    {
        internal string Id {get;}=id;internal string Path {get;}=path;internal long Total {get;}=total;
        internal long Bytes,Updated;private long _notified;private bool _closed;
        /// <summary>显示通知最多每250毫秒一次；状态与移除由任务服务的内存锁保护。</summary>
        internal void Advance(int count)
        {
            bool notify;
            lock(owner._sync)
            {
                if(_closed||owner._disposed||count<=0)return;Bytes=Math.Min(Total,Bytes+count);Updated=Stopwatch.GetTimestamp();
                notify=Stopwatch.GetElapsedTime(_notified,Updated).TotalMilliseconds>=250;if(notify)_notified=Updated;
            }
            if(notify)owner.RaiseChanged();
        }
        public void Dispose(){lock(owner._sync){if(_closed)return;_closed=true;owner._downloadProgress.Remove(this);}owner.RaiseChanged();}
    }
    /// <summary>有界观察集合不影响业务接纳；界面未能采样时仍照常完成原读取。</summary>
    internal DownloadProgress? BeginDownloadProgress(string id,string path,long total)
    {
        if(total<0)return null;DownloadProgress value;
        lock(_sync){if(_disposed||_downloadProgress.Count>=128)return null;value=new(this,id,path,total);_downloadProgress.Add(value);}
        RaiseChanged();return value;
    }
    private MountOperationSnapshot WithDownloadProgress(MountOperationSnapshot value)
    {
        if(value.State is not("Running" or "Downloading" or "Hydrating"))return value;
        lock(_sync)
        {
            var current=_downloadProgress.Where(item=>item.Id==value.Id).MaxBy(item=>item.Updated);
            return current is null?value:value with{RelativeItemPath=current.Path,IsDirectory=false,BytesTransferred=current.Bytes,TotalBytes=current.Total};
        }
    }
}
