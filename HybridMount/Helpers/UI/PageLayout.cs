using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Helpers.UI;
/// <summary>五页统一边距和内容列宽；仅在Loaded期间订阅密度，页面往返及迟到调度不重新创建控件或业务。</summary>
public static class PageLayout
{
    public static readonly DependencyProperty EnabledProperty=DependencyProperty.RegisterAttached("Enabled",typeof(bool),typeof(PageLayout),new PropertyMetadata(false,Changed));
    private static readonly DependencyProperty ScopeProperty=DependencyProperty.RegisterAttached("Scope",typeof(object),typeof(PageLayout),new PropertyMetadata(null));
    public static bool GetEnabled(DependencyObject value)=>(bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value,bool enabled)=>value.SetValue(EnabledProperty,enabled);
    private static void Changed(DependencyObject value,DependencyPropertyChangedEventArgs args)
    {if(value is not Grid grid)return;if(grid.GetValue(ScopeProperty) is Scope old)old.Dispose();grid.SetValue(ScopeProperty,(bool)args.NewValue?new Scope(grid):null);}
    private sealed class Scope:IDisposable
    {
        private readonly Grid _grid;private ApplicationPreferencesService? _preferences;private long _generation;
        public Scope(Grid grid){_grid=grid;grid.Loaded+=Loaded;grid.Unloaded+=Unloaded;}
        private void Loaded(object sender,RoutedEventArgs args){Unsubscribe();_preferences=App.Current.Preferences;_preferences.LayoutChanged+=Apply;Apply();}
        private void Unloaded(object sender,RoutedEventArgs args)=>Unsubscribe();
        private void Unsubscribe(){++_generation;if(_preferences is {} previous)previous.LayoutChanged-=Apply;_preferences=null;}
        private void Apply(){var generation=_generation;if(_grid.DispatcherQueue.HasThreadAccess)Update();else _grid.DispatcherQueue.TryEnqueue(()=>{if(generation==_generation)Update();});}
        private void Update(){if(_preferences is null)return;_grid.Padding=new Thickness(_preferences.CompactLayout?16:24);if(_grid.ColumnDefinitions.Count==0)_grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star),MaxWidth=1200});}
        public void Dispose(){Unsubscribe();_grid.Loaded-=Loaded;_grid.Unloaded-=Unloaded;}
    }
}
