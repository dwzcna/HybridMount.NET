using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Storage.Pickers;
namespace HybridMount.Controls.Mounting;

/// <summary>WebDAV 完整配置对话框；连接、凭据、容量及检测控件在同一 XAML 中，业务由唯一挂载模型拥有。</summary>
public sealed partial class WebDAVConfigurationContentDialog : ContentDialog,IMountConfigurationDialog
{
    private readonly HybridMount.Helpers.UI.FormFieldFeedback _fieldFeedback=new();
    private MountsViewModel _model=null!;
    private MountDefinition? _existing;
    private FileSourceKind _kind;
    private readonly bool _httpsOnly;
    private CancellationTokenSource? _testing;
    private bool _loading=true,_submitting,_picking,_disposed,_stopping,_savingSwitch;
    private string? _httpWarningContext;
    public MountsViewModel ViewModel=>_model;
    public MountDefinition? CreatedMount {get;private set;}
    public string ResultMessage {get;private set;}="";
    public PageMessageSeverity ResultSeverity {get;private set;}=PageMessageSeverity.Information;
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
                AutoConnectField.IsOn=existing.AutoConnect;
                LoadProtocol(existing,model.ReadCredentials(existing.Id),editable);SettingsFields.Visibility=Visibility.Visible;
            }
            EndpointField.TextChanged+=EndpointChanged;
            SettingsFields.Visibility=Visibility.Visible;ThumbnailToggle.Visibility=model.CanConfigureThumbnails?Visibility.Visible:Visibility.Collapsed;
            SaveCapacityButton.Visibility=RecheckButton.Visibility=SaveIntervalsButton.Visibility=existing is null?Visibility.Collapsed:Visibility.Visible;
            if(existing is null){RemoteAuto.IsOn=true;IncrementalField.Value=60;FullField.Value=300;CapacityUnit.SelectedIndex=0;}
            ExclusionList.ItemsSource=model.ExclusionDraft(existing?.Id,_kind);
            SaveExclusionsButton.Visibility=existing is null?Visibility.Collapsed:Visibility.Visible;
            Title=(existing is null?"新增 · ":"设置 · ")+(existing?.Name??(_httpsOnly?"WebDAV(HTTPS)":"WebDAV 挂载"));
            PrimaryButtonText=existing is null?UiTerms.CreateAndConnect:UiTerms.SaveConnectionAndReconnect;SecondaryButtonText=UiTerms.TestConnection;CloseButtonText=UiTerms.Close;
            DefaultButton=existing is null?ContentDialogButton.Primary:ContentDialogButton.Close;
            PrimaryButtonClick+=SaveConnection;SecondaryButtonClick+=TestConnection;Closing+=OnClosing;Closed+=OnClosed;
            model.Refreshed+=Refresh;model.StateChanged+=Refresh;_loading=false;Refresh();UpdateHttpWarning();
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
    private void Refresh()
    {
        if(_disposed)return;
        if(_model.IsStopping){CancelForShutdown();return;}
        var mount=_existing is {} definition?_model.FindMount(definition.Id):null;
        if(_existing is not null&&mount is null){CancelForShutdown();return;}
        var busy=_savingSwitch||_submitting||_picking||_testing is not null||(mount is not null&&!MountActionState.From(mount,_model.IsBusy(mount.Definition.Id)).CanEdit);
        ExclusionFields.IsEnabled=!busy;ConnectionFields.IsEnabled=!busy;SettingsFields.IsEnabled=!busy;
        IsPrimaryButtonEnabled=!busy;IsSecondaryButtonEnabled=!_submitting&&!_picking&&(_testing is not null||!busy);
        if(mount is null||_savingSwitch)return;
        var settings=mount.Definition;var capacity=_model.CapacitySettingsDraft(settings);var detection=_model.Draft(settings);_loading=true;
        try
        {
            AutoConnectField.IsOn=settings.AutoConnect;
            CapacityAuto.Visibility=_kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;CapacityAuto.IsOn=capacity.Automatic;
            MaximumField.Text=capacity.Text;CapacityUnit.SelectedIndex=capacity.Unit=="TB"?1:0;
            MaximumField.IsEnabled=CapacityUnit.IsEnabled=!capacity.Automatic;
            CapacityDescription.Text=MountsViewModel.CapacitySummary(mount);RecheckButton.IsEnabled=mount.IsConnected&&CapacityRules.Enabled(settings);
            ThumbnailToggle.Visibility=_model.CanConfigureThumbnails?Visibility.Visible:Visibility.Collapsed;ThumbnailToggle.IsOn=_model.ThumbnailDraft(settings);ThumbnailDescription.Text=_model.CanConfigureThumbnails?mount.ThumbnailDisplay:"";
            RemoteAuto.IsOn=detection.Enabled;IncrementalField.Value=detection.Incremental;FullField.Value=detection.Full;
            IncrementalField.Visibility=_kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;
            DetectionDescription.Text=MountsViewModel.DetectionSummary(mount);

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
    private async void CapacityModeChanged(object sender,RoutedEventArgs args){if(_loading||Stopped)return;if(_existing is {} mount)await SaveSwitchAsync(()=>_model.SetCapacityAutomaticAsync(mount.Id,CapacityAuto.IsOn));else MaximumField.IsEnabled=CapacityUnit.IsEnabled=!CapacityAuto.IsOn;}
    private void IntervalChanged(NumberBox sender,NumberBoxValueChangedEventArgs args)=>CaptureDetection();
    private async void SaveCapacity(object sender,RoutedEventArgs args){if(Stopped||_existing is null)return;CaptureCapacity();await _model.SaveCapacityAsync(_existing.Id);}
    private async void RecheckCapacity(object sender,RoutedEventArgs args){if(!Stopped&&_existing is {} mount)await _model.RecheckCapacityAsync(mount.Id);}
    private async void SaveIntervals(object sender,RoutedEventArgs args){if(Stopped||_existing is null)return;CaptureDetection();await _model.SaveDetectionAsync(_existing.Id);}
    private async void RemoteAutoChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetRemoteDetectionEnabledAsync(mount.Id,RemoteAuto.IsOn));}
    private async void ThumbnailChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetThumbnailAsync(mount.Id,ThumbnailToggle.IsOn));}
    /// <summary>开关提交期间保留候选值并禁用交互，结束后统一重读正式快照；失败时回滚而不覆盖其他草稿。</summary>
    private async Task SaveSwitchAsync(Func<Task> save)
    {if(Stopped||_savingSwitch)return;_savingSwitch=true;Refresh();try{await save();}finally{_savingSwitch=false;if(!Stopped)Refresh();}}
    private async void AutoConnectChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetAutoConnectAsync(mount.Id,AutoConnectField.IsOn));}
    private MountCreationOptions CreationOptions()
    {
        var incremental=IncrementalField.Value;var full=FullField.Value;
        if(!double.IsFinite(incremental)||incremental!=Math.Truncate(incremental)||!double.IsFinite(full)||full!=Math.Truncate(full))throw new ArgumentException("检测间隔必须为有效的整数秒。");
        return new(ThumbnailToggle.IsOn,RemoteAuto.IsOn,checked((int)incremental),checked((int)full),CapacityAuto.IsOn,CapacityAuto.IsOn?null:CapacityRules.ParseMaximum(MaximumField.Text,CapacityUnit.SelectedIndex==1?"TB":"GB"));
    }
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
        if(!ValidateFields(false))return;using var cancel=new CancellationTokenSource();_testing=cancel;Refresh();SecondaryButtonText="取消测试";
        ErrorBar.IsOpen=false;ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.Message="正在执行只读探测…";ProbeBar.IsOpen=true;
        try
        {
            var result=await _model.TestConnectionAsync(ReadSettings(EndpointField.Text.Trim(),HttpField.IsChecked==true),ReadCredentials(),cancel.Token);
            if(!Stopped){ProbeBar.Severity=result.Success?InfoBarSeverity.Success:InfoBarSeverity.Error;ProbeBar.Message=result.Message;}
        }
        catch(OperationCanceledException){if(!Stopped){ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.Message="连接测试已取消。";}}
        catch(Exception){if(!Stopped){ProbeBar.Severity=InfoBarSeverity.Error;ProbeBar.Message="连接测试失败，请检查配置、网络和凭据来源。";}}
        finally{_testing=null;if(!Stopped){SecondaryButtonText=UiTerms.TestConnection;Refresh();}}
    }
    /// <summary>在禁用控件或发起请求之前校验草稿，字段错误保持原输入并聚焦首个错误。</summary>
    private bool ValidateFields(bool saving)
    {
        _fieldFeedback.Begin();
        if(saving){_fieldFeedback.Check(!string.IsNullOrWhiteSpace(NameField.Text),NameField,UiTerms.NameRequired);_fieldFeedback.Check(Path.IsPathFullyQualified(PathField.Text),PathField,UiTerms.LocalDirectoryRequired);}
        var validAddress=Uri.TryCreate(EndpointField.Text.Trim(),UriKind.Absolute,out var address)&&address.Scheme is "https" or "http";
        _fieldFeedback.Check(validAddress,EndpointField,UiTerms.ServerAddressRequired);
        if(validAddress)_fieldFeedback.Check(address!.Scheme=="https"||HttpField.IsChecked==true,EndpointField,"使用明文 HTTP 需先明确允许；建议改用 HTTPS。");
        if(saving&&_existing is null)
        {
            _fieldFeedback.Check(double.IsFinite(IncrementalField.Value)&&IncrementalField.Value==Math.Truncate(IncrementalField.Value)&&IncrementalField.Value is >=10 and <=86400,IncrementalField,"请输入10至86400范围内的整数秒。");
            _fieldFeedback.Check(double.IsFinite(FullField.Value)&&FullField.Value==Math.Truncate(FullField.Value)&&FullField.Value is >=30 and <=604800,FullField,"请输入30至604800范围内的整数秒。");
            if(!CapacityAuto.IsOn)try{_=CapacityRules.ParseMaximum(MaximumField.Text,CapacityUnit.SelectedIndex==1?"TB":"GB");}catch(ArgumentException error){_fieldFeedback.Check(false,MaximumField,error.Message);}
        }
        if(validAddress&&_httpsOnly)_fieldFeedback.Check(address!.Scheme=="https",EndpointField,"WebDAV(HTTPS) 入口仅接受 HTTPS 地址。");
        if(validAddress)try{_=WebDavFileSource.NormalizeCertificatePin(new(FileSourceKind.WebDav,EndpointField.Text.Trim(),PinnedCertificateSha256:CertificatePinField.Text));}catch(ArgumentException error){_fieldFeedback.Check(false,CertificatePinField,error.Message);}
        return _fieldFeedback.Complete(this);
    }
    private async void SaveConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;if(Stopped||_submitting||_picking||_testing is not null)return;
        if(!ValidateFields(true))return;var deferral=args.GetDeferral();_submitting=true;Refresh();ErrorBar.IsOpen=false;
        try
        {
            var result=await _model.SaveConnectionAsync(_existing?.Id,NameField.Text,PathField.Text,AutoConnectField.IsOn,ReadSettings(EndpointField.Text.Trim(),HttpField.IsChecked==true),ReadCredentials(),exclusions:_existing is null?_model.ReadExclusionRules(null,_kind):null,initial:_existing is null?CreationOptions():null);
            if(!Stopped){CreatedMount=result.Mount;ResultMessage=result.Message;ResultSeverity=result.Severity;args.Cancel=false;}
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
        if(_disposed)return;_disposed=true;_fieldFeedback.Dispose();_testing?.Cancel();ClearSecrets();
        _model.Refreshed-=Refresh;_model.StateChanged-=Refresh;
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
    {if(!_loading){CertificatePinField.Text="";UpdateHttpWarning();}}
    /// <summary>只有用户改变地址才更新明文警告，后台刷新不会重开已关闭提示。</summary>
    private void UpdateHttpWarning(){var current=EndpointField.Text.Trim();if(_httpWarningContext==current)return;_httpWarningContext=current;HttpWarning.IsOpen=Uri.TryCreate(current,UriKind.Absolute,out var address)&&address.Scheme=="http";}
    private void ClearSecrets()=>PasswordField.Password="";
}
