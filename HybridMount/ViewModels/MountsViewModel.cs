using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.UI.Dispatching;

namespace HybridMount.ViewModels;

/// <summary>将后台快照派发到 UI；页面离开缓存后也必须解除订阅。</summary>
public sealed class MountsViewModel : ObservableObject,IDisposable
{
    private readonly MountManager _manager;
    private readonly DispatcherQueue _dispatcher=DispatcherQueue.GetForCurrentThread();
    private int _pending; private bool _disposed;
    private string _status="本地：实心绿勾；仅远端：云；待脱水：空心绿勾，表示允许回收。本地修改不会自动上传。";
    public string Status { get=>_status; set=>SetProperty(ref _status,value); }
    public event Action? Refreshed;
    public ObservableCollection<MountSnapshot> Mounts { get; }=[];

    public MountsViewModel(MountManager manager){_manager=manager;_manager.Changed+=Changed;Refresh();}
    private void Changed()
    {
        if(_disposed||Interlocked.Exchange(ref _pending,1)!=0)return;
        _dispatcher.TryEnqueue(()=>{Interlocked.Exchange(ref _pending,0);if(!_disposed)Refresh();});
    }
    private void Refresh()
    {
        try {Mounts.Clear();foreach(var mount in _manager.Mounts)Mounts.Add(mount);Refreshed?.Invoke();}
        catch(Exception error){Status=error.Message;}
    }
    public void Dispose(){_disposed=true;_manager.Changed-=Changed;}
}
