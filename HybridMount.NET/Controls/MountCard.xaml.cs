using System.ComponentModel;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
namespace HybridMount.Controls;

/// <summary>卡片控件只适配布局与控件事件；动作包含当前行身份，不读取页面选择或操作服务。</summary>
public sealed partial class MountCard : UserControl
{
    public static readonly DependencyProperty ItemProperty=DependencyProperty.Register(nameof(Item),typeof(MountListItem),typeof(MountCard),new PropertyMetadata(null,ItemChanged));
    public MountListItem? Item {get=>(MountListItem?)GetValue(ItemProperty);set=>SetValue(ItemProperty,value);}
    public event EventHandler<MountCardActionEventArgs>? ActionRequested;
    private MountListItem? _observed;
    private bool _initialized;
    private Guid? _flyoutMount;
    public MountCard(){InitializeComponent();_initialized=true;Loaded+=(_,_)=>Observe();Unloaded+=(_,_)=>Detach();}
    private static void ItemChanged(DependencyObject owner,DependencyPropertyChangedEventArgs args)=>((MountCard)owner).Observe();
    private void Observe()
    {
        if(!_initialized)return;
        if(_flyoutMount is {} open&&open!=Item?.Id){_flyoutMount=null;MoreButton.Flyout?.Hide();}
        if(_observed!=Item){Detach();if(IsLoaded&&Item is {} item){_observed=item;item.PropertyChanged+=PresentationChanged;}}
        UpdateSelection();
    }
    private void Detach(){if(_observed is {} item)item.PropertyChanged-=PresentationChanged;_observed=null;_flyoutMount=null;if(_initialized)MoreButton.Flyout?.Hide();}
    private void PresentationChanged(object? sender,PropertyChangedEventArgs args)=>UpdateSelection();
    private void UpdateSelection()
    {if(!_initialized)return;var selected=Item?.IsSelected==true;StatusPanel.Visibility=selected?Visibility.Collapsed:Visibility.Visible;ActionsGrid.Visibility=selected?Visibility.Visible:Visibility.Collapsed;UpdateOverflow(ActionsHost.ActualWidth);}
    private void PerformAction(object sender,RoutedEventArgs args)
    {
        var tag=sender is FrameworkElement element?element.Tag:null;
        if(sender is MenuFlyoutItemBase&&_flyoutMount!=Item?.Id)return;
        var id=sender is MenuFlyoutItemBase?_flyoutMount:Item?.Id;
        if(id is not {} target||tag is not string action)return;
        bool? value=sender switch{ToggleButton toggle=>toggle.IsChecked==true,ToggleMenuFlyoutItem toggle=>toggle.IsChecked,_=>null};
        // 点击先捕获意图，选中态仍显示真实配置；异步保存失败不会留下虚假的已开启状态。
        if(action is "connection" or "thumbnails" or "remoteDetection")
        {
            var actual=action=="connection"?Item?.IsConnected==true:action=="thumbnails"?Item?.ThumbnailsEnabled==true:Item?.RemoteDetectionEnabled==true;
            if(sender is ToggleButton button)button.IsChecked=actual;
            else if(sender is ToggleMenuFlyoutItem menu)menu.IsChecked=actual;
        }
        ActionRequested?.Invoke(this,new(target,action,value));
    }
    // 保留本次打开捕获的身份至下次打开或回收，避免菜单关闭先于 Click 时丢失动作。
    private void OverflowOpening(object sender,object args)=>_flyoutMount=Item?.Id;
    /// <summary>容量列在窄窗缩小；操作只保留能完整显示的图标，其余进入同一个下拉菜单。</summary>
    private void CardSizeChanged(object sender,SizeChangedEventArgs args){if(_initialized)CapacityColumn.Width=new(args.NewSize.Width<700?145:180);}
    private void ActionsSizeChanged(object sender,SizeChangedEventArgs args)=>UpdateOverflow(args.NewSize.Width);
    private void UpdateOverflow(double width)
    {
        if(!_initialized)return;
        FrameworkElement[] buttons=[ConnectionButton,SettingsButton,ScanButton,DetectButton,ThumbnailButton,RemoteDetectionButton,KeepButton,FreeButton,CapacityButton,RemoveButton];
        FrameworkElement[] overflow=[ConnectionOverflow,SettingsOverflow,ScanOverflow,DetectOverflow,ThumbnailOverflow,RemoteDetectionOverflow,KeepOverflow,FreeOverflow,CapacityOverflow,RemoveOverflow];
        int[] boundaries=[2,4,6,9];
        var active=Enumerable.Range(0,buttons.Length).Where(index=>index!=4||Item?.CanConfigureThumbnails==true).ToArray();
        double RequiredWidth(int count,bool menu)=>count==0?(menu?48:0):count*36+(count-1)*6+boundaries.Count(boundary=>active.Take(count).Any(x=>x<boundary)&&active.Take(count).Any(x=>x>=boundary))*7+(menu?54:0);
        var showAll=width>=RequiredWidth(active.Length,false);var visible=showAll?active.Length:active.Length-1;
        while(!showAll&&visible>0&&RequiredWidth(visible,true)>Math.Max(0,width))visible--;
        if(showAll)MoreButton.Flyout?.Hide();MoreButton.Visibility=showAll?Visibility.Collapsed:Visibility.Visible;
        foreach(var control in buttons.Concat(overflow))control.Visibility=Visibility.Collapsed;
        for(var index=0;index<active.Length;index++){var item=active[index];buttons[item].Visibility=index<visible?Visibility.Visible:Visibility.Collapsed;overflow[item].Visibility=index<visible?Visibility.Collapsed:Visibility.Visible;}
        Border[] separators=[ConnectionSeparator,ManualSeparator,OptionsSeparator,StorageSeparator];
        MenuFlyoutSeparator[] menuSeparators=[ConnectionOverflowSeparator,ManualOverflowSeparator,OverflowSeparator,StorageOverflowSeparator];
        for(var index=0;index<boundaries.Length;index++)
        {
            var boundary=boundaries[index];
            separators[index].Visibility=active.Take(visible).Any(x=>x<boundary)&&active.Take(visible).Any(x=>x>=boundary)?Visibility.Visible:Visibility.Collapsed;
            menuSeparators[index].Visibility=active.Skip(visible).Any(x=>x<boundary)&&active.Skip(visible).Any(x=>x>=boundary)?Visibility.Visible:Visibility.Collapsed;
        }
    }

}
/// <summary>当前卡片的交互意图；页面负责展示对话框或转交唯一ViewModel。</summary>
public sealed class MountCardActionEventArgs(Guid mountId,string action,bool? value=null):EventArgs
{public Guid MountId {get;}=mountId;public string Action {get;}=action;public bool? Value {get;}=value;}
