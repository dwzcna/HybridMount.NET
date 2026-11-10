using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Storage.Pickers;
namespace HybridMount.Controls.Mounting;

/// <summary>WebDAV 完整配置对话框；连接、凭据、容量及检测控件在同一 XAML 中，业务由唯一挂载模型拥有。</summary>
public sealed partial class WebDAVConfigurationContentDialog : ContentDialog,IMountConfigurationDialog
{
    private MountsViewModel _model=null!;
    private MountDefinition? _existing;
    private FileSourceKind _kind;
    private readonly bool _httpsOnly;
    private CancellationTokenSource? _testing;
    private bool _loading=true,_submitting,_picking,_disposed,_stopping;
    public MountDefinition? CreatedMount {get;private set;}
    public string ResultMessage {get;private set;}="";
    private bool Stopped=>_disposed||_stopping||_model.IsStopping;
    /// <summary>只加载一次连接草稿；后续刷新仅更新已保存设置，不覆盖正在编辑的账号或地址。</summary>
    public WebDAVConfigurationContentDialog(MountsViewModel model,MountDefinition? existing=null,bool httpsOnly=false)
    {
        InitializeComponent();_model=model;_kind=FileSourceKind.WebDav;_existing=existing;
        _httpsOnly=httpsOnly||existing?.Source?.PinnedCertificateSha256 is not null;
        try
        {
            NameField.Text=existing?.Name??(_httpsOnly?"WebDAV(HTTPS) 挂载":"WebDAV 挂载");
            EndpointField.PlaceholderText=_httpsOnly?"https://nas.example.com:5006/共享文件夹/":"https://dav.example.com/dav/";
            HttpsFields.Visibility=_httpsOnly?Visibility.Visible:Visibility.Collapsed;
            HttpField.Visibility=_httpsOnly?Visibility.Collapsed:Visibility.Visible;
            if(existing is not null)
            {
                var editable=false;var source=existing.Source!;
                NameField.IsReadOnly=true;PathField.Text=existing.LocalPath;ChooseButton.IsEnabled=false;
                EndpointField.Text=source.Endpoint;EndpointField.IsReadOnly=!editable;HttpField.IsChecked=source.AllowHttp;HttpField.IsEnabled=editable;
                CertificatePinField.Text=source.PinnedCertificateSha256??"";
                AutoConnectField.IsOn=existing.AutoConnect;AutoConnectField.IsEnabled=false;
                LoadProtocol(existing,model.ReadCredentials(existing.Id),editable);SettingsFields.Visibility=Visibility.Visible;
            }
            EndpointField.TextChanged+=EndpointChanged;
            ExclusionList.ItemsSource=model.ExclusionDraft(existing?.Id,_kind);
            SaveExclusionsButton.Visibility=existing is null?Visibility.Collapsed:Visibility.Visible;
            Title=(existing is null?"新增 · ":"设置 · ")+(existing?.Name??(_httpsOnly?"WebDAV(HTTPS)":"WebDAV 挂载"));
            PrimaryButtonText=existing is null?"创建并连接":"保存连接并重连";SecondaryButtonText="测试连接";CloseButtonText="关闭";
            DefaultButton=existing is null?ContentDialogButton.Primary:ContentDialogButton.Close;
            PrimaryButtonClick+=SaveConnection;SecondaryButtonClick+=TestConnection;Closing+=OnClosing;Closed+=OnClosed;
            model.Refreshed+=Refresh;model.StateChanged+=Refresh;model.PropertyChanged+=ModelPropertyChanged;_loading=false;Refresh();
            if(existing is not null)_=LoadRemoteRootEditabilityAsync(existing.Id);
        }
        catch{Dispose();throw;}
    }
    /// <summary>后台核验可编辑性，返回窗口线程后再次检查生命周期，关闭后不更新任何控件。</summary>
    private async Task LoadRemoteRootEditabilityAsync(Guid id)
    {
        var dispatcher=DispatcherQueue;
        try
        {
            var editable=await _model.CanEditRemoteRootAsync(id).ConfigureAwait(false);
            dispatcher.TryEnqueue(()=>{if(Stopped)return;EndpointField.IsReadOnly=!editable;HttpField.IsEnabled=editable;});
        }
        catch(Exception error){dispatcher.TryEnqueue(()=>{if(Stopped)return;ErrorBar.Message=error.Message;ErrorBar.IsOpen=true;});}
    }
    private void ModelPropertyChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs args){if(!_disposed&&args.PropertyName==nameof(MountsViewModel.Status))StatusText.Text=_model.Status;}
    private void Refresh()
    {
        if(_disposed)return;
        if(_model.IsStopping){CancelForShutdown();return;}
        var mount=_existing is {} definition?_model.FindMount(definition.Id):null;
        if(_existing is not null&&mount is null){CancelForShutdown();return;}
        var busy=_submitting||_picking||_testing is not null||(mount is not null&&!MountActionState.From(mount,_model.IsBusy(mount.Definition.Id)).CanEdit);
        ExclusionFields.IsEnabled=!busy;ConnectionFields.IsEnabled=!busy;SettingsFields.IsEnabled=!busy;
        IsPrimaryButtonEnabled=!busy;IsSecondaryButtonEnabled=!_submitting&&!_picking&&(_testing is not null||!busy);
        if(mount is null)return;
        var settings=mount.Definition;var capacity=_model.CapacitySettingsDraft(settings);var detection=_model.Draft(settings);_loading=true;
        try
        {
            CapacityAuto.Visibility=_kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;CapacityAuto.IsOn=capacity.Automatic;
            MaximumField.Text=capacity.Text;CapacityUnit.SelectedIndex=capacity.Unit=="TB"?1:0;
            MaximumField.IsEnabled=CapacityUnit.IsEnabled=!capacity.Automatic;
            CapacityDescription.Text=MountsViewModel.CapacitySummary(mount);RecheckButton.IsEnabled=mount.IsConnected&&CapacityRules.Enabled(settings);
            ThumbnailToggle.Visibility=_model.CanConfigureThumbnails?Visibility.Visible:Visibility.Collapsed;ThumbnailToggle.IsOn=_model.ThumbnailDraft(settings);ThumbnailDescription.Text=_model.CanConfigureThumbnails?mount.ThumbnailDisplay:"";
            RemoteAuto.IsOn=detection.Enabled;IncrementalField.Value=detection.Incremental;FullField.Value=detection.Full;
            IncrementalField.Visibility=_kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;
            DetectionDescription.Text=MountsViewModel.DetectionSummary(mount);
            StatusText.Text=_model.Status;
        }
        finally{_loading=false;}
    }
    private void AddExclusionRule(object sender,RoutedEventArgs args){if(!Stopped)_model.AddExclusionRule(_existing?.Id,_kind);}
    private void AddDefaultExclusionRules(object sender,RoutedEventArgs args){if(!Stopped)_model.AddDefaultExclusionRules(_existing?.Id,_kind);}
    private void RemoveExclusionRule(object sender,RoutedEventArgs args){if(!Stopped&&sender is FrameworkElement{Tag:FileExclusionRuleDraft row})_model.RemoveExclusionRule(_existing?.Id,_kind,row);}
    private async void SaveExclusions(object sender,RoutedEventArgs args){if(!Stopped&&_existing is {} mount)await _model.SaveExclusionsAsync(mount.Id);}
    private void CaptureCapacity(){if(!_loading&&!Stopped&&_existing is {} mount)_model.UpdateCapacityDraft(mount.Id,CapacityAuto.IsOn,MaximumField.Text,CapacityUnit.SelectedIndex==1?"TB":"GB");}
    private void CaptureDetection(){if(!_loading&&!Stopped&&_existing is {} mount)_model.UpdateDraft(mount.Id,RemoteAuto.IsOn,IncrementalField.Value,FullField.Value);}
    private void CapacityTextChanged(object sender,TextChangedEventArgs args)=>CaptureCapacity();
    private void CapacityUnitChanged(object sender,SelectionChangedEventArgs args)=>CaptureCapacity();
    private void CapacityModeChanged(object sender,RoutedEventArgs args){if(_loading)return;CaptureCapacity();Refresh();}
    private void IntervalChanged(NumberBox sender,NumberBoxValueChangedEventArgs args)=>CaptureDetection();
    private async void SaveCapacity(object sender,RoutedEventArgs args){if(Stopped||_existing is null)return;CaptureCapacity();await _model.SaveCapacityAsync(_existing.Id);}
    private async void RecheckCapacity(object sender,RoutedEventArgs args){if(!Stopped&&_existing is {} mount)await _model.RecheckCapacityAsync(mount.Id);}
    private async void SaveIntervals(object sender,RoutedEventArgs args){if(Stopped||_existing is null)return;CaptureDetection();await _model.SaveDetectionAsync(_existing.Id);}
    private async void RemoteAutoChanged(object sender,RoutedEventArgs args){if(_loading||Stopped||_existing is null)return;CaptureDetection();await _model.SaveDetectionAsync(_existing.Id);}
    private async void ThumbnailChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await _model.SetThumbnailAsync(mount.Id,ThumbnailToggle.IsOn);}
    private async void ChooseFolder(object sender,RoutedEventArgs args)
    {
        if(Stopped||_submitting||_picking||_testing is not null)return;_picking=true;Refresh();
        try
        {
            var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
            var folder=await picker.PickSingleFolderAsync();if(!Stopped&&folder is not null)PathField.Text=folder.Path;
        }
        catch(Exception error){if(!Stopped){ErrorBar.Message=error.Message;ErrorBar.IsOpen=true;}}
        finally{_picking=false;if(!Stopped)Refresh();}
    }
    /// <summary>只读测试可由同一按钮取消；正文和请求由应用拥有，关闭只取消、不提前释放令牌。</summary>
    private async void TestConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;if(Stopped)return;
        if(_testing is {} testing){SecondaryButtonText="取消中…";IsSecondaryButtonEnabled=false;testing.Cancel();return;}
        if(_submitting||_picking)return;
        using var cancel=new CancellationTokenSource();_testing=cancel;Refresh();SecondaryButtonText="取消测试";
        ErrorBar.IsOpen=false;ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.Message="正在执行只读探测…";ProbeBar.IsOpen=true;
        try
        {
            var result=await _model.TestConnectionAsync(ReadSettings(EndpointField.Text.Trim(),HttpField.IsChecked==true),ReadCredentials(),cancel.Token);
            if(!Stopped){ProbeBar.Severity=result.Success?InfoBarSeverity.Success:InfoBarSeverity.Error;ProbeBar.Message=result.Message;}
        }
        catch(OperationCanceledException){if(!Stopped){ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.Message="连接测试已取消。";}}
        catch(Exception){if(!Stopped){ProbeBar.Severity=InfoBarSeverity.Error;ProbeBar.Message="连接测试失败，请检查配置、网络和凭据来源。";}}
        finally{_testing=null;if(!Stopped){SecondaryButtonText="测试连接";Refresh();}}
    }
    private async void SaveConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;if(Stopped||_submitting||_picking||_testing is not null)return;
        var deferral=args.GetDeferral();_submitting=true;Refresh();ErrorBar.IsOpen=false;
        try
        {
            var result=await _model.SaveConnectionAsync(_existing?.Id,NameField.Text,PathField.Text,AutoConnectField.IsOn,ReadSettings(EndpointField.Text.Trim(),HttpField.IsChecked==true),ReadCredentials(),exclusions:_existing is null?_model.ReadExclusionRules(null,_kind):null);
            if(!Stopped){CreatedMount=result.Mount;ResultMessage=result.Message;args.Cancel=false;}
        }
        catch(OperationCanceledException)when(_model.IsStopping){args.Cancel=false;}
        catch(Exception error){if(!Stopped){ErrorBar.Message=error.Message;ErrorBar.IsOpen=true;}}
        finally{_submitting=false;if(!Stopped)Refresh();deferral.Complete();}
    }
    private void OnClosing(ContentDialog sender,ContentDialogClosingEventArgs args)
    {if(_testing is {} testing)testing.Cancel();if(!_stopping&&(_submitting||_picking||(_existing is {} mount&&_model.IsBusy(mount.Id))))args.Cancel=true;}
    private void OnClosed(ContentDialog sender,ContentDialogClosedEventArgs args)=>Dispose();
    /// <summary>退出强制关闭视图，应用继续等待网络任务；迟到结果不更新已释放控件。</summary>
    public void CancelForShutdown(){if(_disposed)return;_stopping=true;_testing?.Cancel();Hide();Dispose();}
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_testing?.Cancel();ClearSecrets();
        _model.Refreshed-=Refresh;_model.StateChanged-=Refresh;_model.PropertyChanged-=ModelPropertyChanged;
        EndpointField.TextChanged-=EndpointChanged;
        PrimaryButtonClick-=SaveConnection;SecondaryButtonClick-=TestConnection;Closing-=OnClosing;Closed-=OnClosed;
    }
    /// <summary>在窗口线程读取草稿；HTTPS 预设拒绝明文地址，指纹由协议层校验且仅属于当前挂载。</summary>
    private FileSourceConfiguration ReadSettings(string endpoint,bool allowHttp)
    {
        if(_httpsOnly&&(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"))
            throw new ArgumentException("WebDAV(HTTPS) 入口仅接受 HTTPS 地址；群晖通常使用 5006 端口。");
        return new(FileSourceKind.WebDav,endpoint,AllowHttp:!_httpsOnly&&allowHttp,
            PinnedCertificateSha256:string.IsNullOrWhiteSpace(CertificatePinField.Text)?null:CertificatePinField.Text.Trim());
    }
    private SourceCredentials ReadCredentials()=>new(UserField.Text.Trim(),PasswordField.Password);
    private void LoadProtocol(MountDefinition definition,SourceCredentials credentials,bool rootEditable){UserField.Text=credentials.UserName;PasswordField.Password=credentials.Password;}
    /// <summary>地址变更立即撤销旧指纹草稿，防止将旧服务器授权带到新地址；初始化加载不触发撤销。</summary>
    private void EndpointChanged(object sender,TextChangedEventArgs args)
    {if(!_loading)CertificatePinField.Text="";}
    private void ClearSecrets()=>PasswordField.Password="";
}
