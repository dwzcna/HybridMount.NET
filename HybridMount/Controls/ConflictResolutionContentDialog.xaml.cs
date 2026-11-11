using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Controls;

/// <summary>任务页冲突比较和直接取舍视图；所有业务状态、取消和防重入均由TasksViewModel拥有。</summary>
public sealed partial class ConflictResolutionContentDialog:ContentDialog
{
    private IDisposable? _localization;
    public TasksViewModel ViewModel{get;}
    public ConflictResolutionContentDialog(TasksViewModel viewModel){ViewModel=viewModel;InitializeComponent();_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(this,()=>Bindings.Update());}
    private async void RefreshComparison(object sender,RoutedEventArgs args)=>await ViewModel.RefreshConflictAsync();
    private async void UseLocal(ContentDialog sender,ContentDialogButtonClickEventArgs args){var deferral=args.GetDeferral();try{args.Cancel=!await ViewModel.ResolveConflictAsync(true);}finally{deferral.Complete();}}
    private async void UseRemote(ContentDialog sender,ContentDialogButtonClickEventArgs args){var deferral=args.GetDeferral();try{args.Cancel=!await ViewModel.ResolveConflictAsync(false);}finally{deferral.Complete();}}
}
