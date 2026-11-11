using HybridMount.Helpers.UI;
using Microsoft.UI.Xaml.Navigation;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Storage;
using global::Windows.Storage.Pickers;

namespace HybridMount.Pages;

/// <summary>最近日志的筛选、复制、导出和确认清除入口。</summary>
public sealed partial class LogsPage : Page, IDisposable
{
    private IDisposable? _localization;
    private HeaderLayoutCoordinator? _header;
    /// <summary>仅该页面拥有日志展示订阅。</summary>
    public LogsViewModel ViewModel { get; } = new(App.Current.Logger,App.Current.UiDispatcher);
    /// <summary>页面出现时重新读取 SQLite 快照。</summary>
    private readonly PageScrollBookmark _viewport=new();
    public LogsPage(){try{InitializeComponent();_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(this,()=>{Bindings.Update();_header?.Invalidate();});NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}_header=new(LogToolbar,LogFilters,LogActions,SetFilterLabels);Loaded+=PageLoaded;}
    protected override void OnNavigatedTo(NavigationEventArgs args){base.OnNavigatedTo(args);_viewport.Enter(LogList);}
    protected override void OnNavigatedFrom(NavigationEventArgs args){_viewport.Leave(LogList);base.OnNavigatedFrom(args);}
    private async void PageLoaded(object sender,RoutedEventArgs args){_viewport.Restore(LogList);await ViewModel.RefreshAsync(reopenStatus:false);}
    /// <summary>只收起筛选文字；图标、提示、读屏名称和当前选择保留。</summary>
    private void SetFilterLabels(bool compact)
    {
        foreach(var label in new[]{AllFilterLabel,InformationFilterLabel,WarningFilterLabel,ErrorFilterLabel})label.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
    }
    private void ClearDateClicked(object sender,RoutedEventArgs args)=>ViewModel.SelectedDate=null;
    /// <summary>按实际内容宽度换行日期区域，保留搜索与日期完整可用；不依赖窗口外层导航宽度。</summary>
    private void QueryHeaderSizeChanged(object sender,SizeChangedEventArgs args)=>ReflowQueryHeader();
    private void DateFilterSizeChanged(object sender,SizeChangedEventArgs args)=>ReflowQueryHeader();
    private void ReflowQueryHeader()
    {
        if(DateFilter is null||LogSearch is null)return;
        DateFilter.Measure(new(double.PositiveInfinity,double.PositiveInfinity));
        var inline=QueryHeader.ActualWidth>=LogSearch.MinWidth+DateFilter.DesiredSize.Width+QueryHeader.ColumnSpacing;
        Grid.SetColumnSpan(LogSearch,inline?1:2);Grid.SetColumn(DateFilter,inline?1:0);
        Grid.SetColumnSpan(DateFilter,inline?1:2);Grid.SetRow(DateFilter,inline?0:1);
    }
    private async void RefreshClicked(object sender, RoutedEventArgs args) => await ViewModel.RefreshAsync();
    private async void OlderClicked(object sender,RoutedEventArgs args)=>await ViewModel.LoadOlderAsync();
    /// <summary>只打开本应用安全生命周期证据目录，不附带注册表、连接配置或正文备份。</summary>
    private async void LifecycleClicked(object sender,RoutedEventArgs args)
    {
        try{Directory.CreateDirectory(ApplicationPaths.LogsDirectory);await global::Windows.System.Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(ApplicationPaths.LogsDirectory));}
        catch(Exception error){ViewModel.ReportError(error);}
    }
    private void CopyClicked(object sender, RoutedEventArgs args)
    {
        try { var package = new DataPackage(); package.SetText(ViewModel.ExportText()); Clipboard.SetContent(package); }
        catch (Exception error) { ViewModel.ReportError(error); }
    }
    private async void ExportClicked(object sender, RoutedEventArgs args)
    {
        if(!ViewModel.TryBeginDialog())return;
        try
        {
            var query=ViewModel.CaptureExportQuery();
            var picker = new FileSavePicker { SuggestedFileName = $"HybridMount-{DateTime.Now:yyyyMMdd-HHmmss}" };
            picker.FileTypeChoices.Add(global::HybridMount.Services.Localization.UiText.Text("Text_97E3BE741179"), new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindow!.Handle);
            var file = await picker.PickSaveFileAsync();
            if(file is not null&&!App.Current.IsExiting)
            {var export=await ViewModel.ExportAsync(query);if(!App.Current.IsExiting)await FileIO.WriteTextAsync(file,export.Text);}
        }
        catch (Exception error) { ViewModel.ReportError(error); }
        finally {ViewModel.EndDialog();}
    }
    private async void ClearClicked(object sender, RoutedEventArgs args)
    {
        if(!ViewModel.TryBeginDialog())return;
        try
        {
            var dialog = global::HybridMount.Helpers.UI.Ui.Configure(new ContentDialog { XamlRoot = XamlRoot,DefaultButton = ContentDialogButton.Close },("Title",global::HybridMount.Services.Localization.UiText.Message("Text_02712916DEE9")),("Content",global::HybridMount.Services.Localization.UiText.Message("Text_66456857A9CF")),("PrimaryButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_BCE2377283C2")),("CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_2CD0F3BE8738")));
            if (await global::HybridMount.Helpers.UI.Ui.ShowAsync(dialog) == ContentDialogResult.Primary) await ViewModel.ClearAsync();
        }
        catch (Exception error) { ViewModel.ReportError(error); }
        finally {ViewModel.EndDialog();}
    }
    /// <summary>真正退出时解除日志订阅。</summary>
    public void Dispose(){_header?.Dispose();_header=null;_localization?.Dispose();_localization=null;Loaded-=PageLoaded;_viewport.Stop();ViewModel.Dispose();}
}
