using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Helpers.UI;

/// <summary>UI线程的共享页头排版；只移动既有命令到原生溢出区，保留事件、菜单及读屏身份。</summary>
internal sealed class HeaderLayoutCoordinator:IDisposable
{
    private readonly Grid _root;
    private readonly FrameworkElement _filters;
    private readonly CommandBar _actions;
    private readonly Action<bool> _setCompact;
    private readonly AppBarButton[] _commands;
    private readonly List<(AppBarButton Button,DependencyProperty Property,long Token)> _tokens=[];
    private bool _dirty=true,_pending,_disposed;
    private long _generation;
    private double _compact,_expanded;
    private double[] _widths=[];
    private HeaderLayoutChoice? _previous;
    /// <summary>在InitializeComponent之后登记；所有调用与释放均由所属页面UI线程完成。</summary>
    internal HeaderLayoutCoordinator(Grid root,FrameworkElement filters,CommandBar actions,Action<bool> setCompact)
    {
        _root=root;_filters=filters;_actions=actions;_setCompact=setCompact;
        _commands=actions.PrimaryCommands.OfType<AppBarButton>().ToArray();
        actions.IsDynamicOverflowEnabled=false;
        root.Loaded+=Loaded;root.Unloaded+=Unloaded;root.SizeChanged+=Resized;
        foreach(var button in _commands)foreach(var property in new[]{UIElement.VisibilityProperty,AppBarButton.LabelProperty})
            _tokens.Add((button,property,button.RegisterPropertyChangedCallback(property,(_,_)=>Invalidate())));
        Invalidate();
    }
    private void Loaded(object sender,RoutedEventArgs args)=>Invalidate();
    private void Unloaded(object sender,RoutedEventArgs args){++_generation;_pending=false;}
    private void Resized(object sender,SizeChangedEventArgs args)=>Queue();
    /// <summary>语言、文字或可见项变化后重测实际宽度；合并低优先级更新并屏蔽卸载后的迟到调用。</summary>
    internal void Invalidate(){_dirty=true;Queue();}
    private void Queue()
    {
        if(_disposed||_pending)return;_pending=true;var generation=_generation;
        if(!_root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,()=>
        {
            if(_disposed||generation!=_generation)return;_pending=false;
            if(!_root.IsLoaded||_root.ActualWidth<=0)return;
            Arrange();
        }))_pending=false;
    }
    private void Arrange()
    {
        if(_dirty)
        {
            _dirty=false;
            // 将命令临时放回主区测量原生右侧标签模板，整个过程在同一UI调用内完成，不创建替代按钮。
            _actions.SecondaryCommands.Clear();_actions.PrimaryCommands.Clear();
            foreach(var button in _commands){Ui.RefreshResources(button);_actions.PrimaryCommands.Add(button);}
            _actions.OverflowButtonVisibility=CommandBarOverflowButtonVisibility.Collapsed;
            _actions.Measure(new(double.PositiveInfinity,double.PositiveInfinity));
            _widths=_commands.Where(x=>x.Visibility==Visibility.Visible).Select(x=>{x.Measure(new(double.PositiveInfinity,double.PositiveInfinity));return Math.Ceiling(Math.Max(40,x.DesiredSize.Width));}).ToArray();
            _setCompact(false);_filters.UpdateLayout();_filters.Measure(new(double.PositiveInfinity,double.PositiveInfinity));_expanded=Math.Ceiling(_filters.DesiredSize.Width);
            _setCompact(true);_filters.UpdateLayout();_filters.Measure(new(double.PositiveInfinity,double.PositiveInfinity));_compact=Math.Ceiling(_filters.DesiredSize.Width);
        }
        var choice=HeaderLayoutPolicy.Choose(_root.ActualWidth,_compact,_expanded,_widths,_previous,gap:_root.ColumnSpacing);
        _previous=choice;_setCompact(!choice.ShowLabels);
        var visible=_commands.Where(x=>x.Visibility==Visibility.Visible).ToArray();
        var primary=visible.Take(choice.Count).ToArray();
        var secondary=_commands.Except(primary).ToArray();
        if(!_actions.PrimaryCommands.SequenceEqual(primary)||!_actions.SecondaryCommands.SequenceEqual(secondary))
        {
            _actions.PrimaryCommands.Clear();_actions.SecondaryCommands.Clear();
            foreach(var button in primary)_actions.PrimaryCommands.Add(button);
            foreach(var button in secondary)_actions.SecondaryCommands.Add(button);
        }
        _actions.HorizontalAlignment=HorizontalAlignment.Right;
        _actions.OverflowButtonVisibility=choice.Count<visible.Length?CommandBarOverflowButtonVisibility.Visible:CommandBarOverflowButtonVisibility.Collapsed;
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;++_generation;
        _root.Loaded-=Loaded;_root.Unloaded-=Unloaded;_root.SizeChanged-=Resized;
        foreach(var item in _tokens)item.Button.UnregisterPropertyChangedCallback(item.Property,item.Token);
        _tokens.Clear();
    }
}
