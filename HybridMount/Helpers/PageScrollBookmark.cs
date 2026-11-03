using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
namespace HybridMount.Helpers;

/// <summary>只保存视图滚动位置；隐藏页不覆盖书签，低优先级恢复重验导航代次，停止后丢弃排队回调。</summary>
internal sealed class PageScrollBookmark
{
    private ScrollViewer? _viewer;
    private double _offset;
    private bool _active,_stopped;
    private long _revision;
    public void Enter(FrameworkElement root){if(_stopped)return;_active=true;Restore(root);}
    public void Leave(FrameworkElement root){Capture(root);_active=false;_revision++;}
    public void Capture(FrameworkElement root){if(!_active||_stopped)return;_viewer=Find(root);if(_viewer is not null)_offset=_viewer.VerticalOffset;}
    public void Reset(){_offset=0;_revision++;}
    public void Restore(FrameworkElement root)
    {
        if(!_active||_stopped)return;var revision=++_revision;
        root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,()=>
        {
            if(!_active||_stopped||revision!=_revision)return;
            _viewer=Find(root);_viewer?.ChangeView(null,_offset,null,true);
        });
    }
    public void Stop(){_stopped=true;_active=false;_revision++;_viewer=null;}
    private static ScrollViewer? Find(DependencyObject parent)
    {
        if(parent is ScrollViewer scroll)return scroll;
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)if(Find(VisualTreeHelper.GetChild(parent,index)) is {} nested)return nested;
        return null;
    }
}
