using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;
namespace HybridMount.Controls;
/// <summary>共享设置行只负责布局和读屏标签，窄窗口将编辑器移到下一行，业务保存仍由页面拥有。</summary>
[ContentProperty(Name=nameof(Setting))]
public sealed partial class SettingRow:UserControl
{
    private IDisposable? _localization;
    public static readonly DependencyProperty HeaderProperty=DependencyProperty.Register(nameof(Header),typeof(string),typeof(SettingRow),new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty=DependencyProperty.Register(nameof(Description),typeof(string),typeof(SettingRow),new PropertyMetadata(""));
    public static readonly DependencyProperty SettingProperty=DependencyProperty.Register(nameof(Setting),typeof(object),typeof(SettingRow),new PropertyMetadata(null));
    public string Header{get=>(string)GetValue(HeaderProperty);set=>SetValue(HeaderProperty,value);}
    public string Description{get=>(string)GetValue(DescriptionProperty);set=>SetValue(DescriptionProperty,value);}
    public object? Setting{get=>GetValue(SettingProperty);set=>SetValue(SettingProperty,value);}
    private ApplicationPreferencesService? _preferences;private long _generation;
    public SettingRow(){InitializeComponent();_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(this,()=>Bindings.Update());Loaded+=OnLoaded;Unloaded+=OnUnloaded;}
    private void OnLoaded(object sender,RoutedEventArgs args){Unsubscribe();_preferences=App.Current.Preferences;_preferences.LayoutChanged+=Refresh;Refresh();if(Setting is FrameworkElement field)AutomationProperties.SetLabeledBy(field,Label);}
    private void OnUnloaded(object sender,RoutedEventArgs args)=>Unsubscribe();
    private void Unsubscribe(){++_generation;if(_preferences is {} previous)previous.LayoutChanged-=Refresh;_preferences=null;}
    private void Refresh(){var generation=_generation;if(!DispatcherQueue.HasThreadAccess){DispatcherQueue.TryEnqueue(()=>{if(_preferences is not null&&generation==_generation)Refresh();});return;}if(_preferences is null)return;Row.Padding=new Thickness(0,_preferences.CompactLayout?4:10,0,_preferences.CompactLayout?4:10);Arrange();}
    private void Resized(object sender,SizeChangedEventArgs args)=>Arrange();
    private void Arrange(){if(Row is null)return;var narrow=ActualWidth<560;Grid.SetColumnSpan(TextHost,narrow?2:1);Grid.SetRow(ValueHost,narrow?1:0);Grid.SetColumn(ValueHost,narrow?0:1);Grid.SetColumnSpan(ValueHost,narrow?2:1);ValueHost.HorizontalAlignment=narrow?HorizontalAlignment.Stretch:HorizontalAlignment.Right;}
}
