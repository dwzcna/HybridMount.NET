using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Storage.Pickers;
namespace HybridMount.Controls.Mounting;

/// <summary>S3 完整配置对话框；连接、凭据、容量及检测控件在同一 XAML 中，业务由唯一挂载模型拥有。</summary>
public sealed partial class S3ConfigurationContentDialog : ContentDialog,IMountConfigurationDialog
{
    private readonly HybridMount.Helpers.UI.FormFieldFeedback _fieldFeedback=new();
    private MountsViewModel _model=null!;
    private MountDefinition? _existing;
    private FileSourceKind _kind;
    private CancellationTokenSource? _testing,_sqsProbe;
    private bool _sqsSaving;
    private bool _loading=true,_submitting,_picking,_disposed,_stopping,_savingSwitch;
    private string? _httpWarningContext;
    public MountsViewModel ViewModel=>_model;
    public MountDefinition? CreatedMount {get;private set;}
    public string ResultMessage {get;private set;}="";
    public PageMessageSeverity ResultSeverity {get;private set;}=PageMessageSeverity.Information;
    private bool Stopped=>_disposed||_stopping||_model.IsStopping;
    /// <summary>只加载一次连接草稿；后续刷新仅更新已保存设置，不覆盖正在编辑的账号或地址。</summary>
    public S3ConfigurationContentDialog(MountsViewModel model,MountDefinition? existing=null)
    {
        InitializeComponent();_model=model;_kind=FileSourceKind.S3;_existing=existing;
        try
        {
            NameField.Text=existing?.Name??(_kind==FileSourceKind.S3?"S3 挂载":"WebDAV 挂载");
            EndpointField.PlaceholderText=_kind==FileSourceKind.S3?"https://s3.example.com":"https://dav.example.com/dav/";
            if(existing is not null)
            {
                var editable=false;var source=existing.Source!;
                NameField.IsReadOnly=true;PathField.Text=existing.LocalPath;ChooseButton.IsEnabled=false;
                EndpointField.Text=source.Endpoint;EndpointField.IsReadOnly=!editable;HttpField.IsChecked=source.AllowHttp;HttpField.IsEnabled=editable;
                AutoConnectField.IsOn=existing.AutoConnect;
                LoadProtocol(existing,model.ReadCredentials(existing.Id),editable);LoadSqs(existing);SettingsFields.Visibility=Visibility.Visible;
            }
            SettingsFields.Visibility=Visibility.Visible;ThumbnailToggle.Visibility=model.CanConfigureThumbnails?Visibility.Visible:Visibility.Collapsed;
            SaveCapacityButton.Visibility=RecheckButton.Visibility=SaveIntervalsButton.Visibility=existing is null?Visibility.Collapsed:Visibility.Visible;
            if(existing is null){RemoteAuto.IsOn=true;IncrementalField.Value=60;FullField.Value=300;CapacityUnit.SelectedIndex=0;}
            CapacityAuto.Visibility=Visibility.Collapsed;if(existing is null){SqsMode.SelectedIndex=0;SqsTestButton.Visibility=SqsSaveButton.Visibility=Visibility.Collapsed;}
            EndpointField.TextChanged+=EndpointChanged;
            ExclusionList.ItemsSource=model.ExclusionDraft(existing?.Id,_kind);
            SaveExclusionsButton.Visibility=existing is null?Visibility.Collapsed:Visibility.Visible;
            Title=(existing is null?"新增":"设置 · ")+(existing?.Name??(_kind==FileSourceKind.S3?"S3 挂载":"WebDAV 挂载"));
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
            dispatcher.TryEnqueue(()=>{if(Stopped)return;EndpointField.IsReadOnly=!editable;HttpField.IsEnabled=editable;BucketField.IsReadOnly=!editable;PrefixField.IsReadOnly=!editable;});
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
        var sqsBusy=_sqsProbe is not null||_sqsSaving;
        ExclusionFields.IsEnabled=!busy&&!sqsBusy;ConnectionFields.IsEnabled=!busy&&!sqsBusy;SettingsFields.IsEnabled=!busy;
        IsPrimaryButtonEnabled=!busy&&!sqsBusy;IsSecondaryButtonEnabled=!_submitting&&!_picking&&!sqsBusy&&(_testing is not null||!busy);
        SqsFields.IsEnabled=!sqsBusy;SqsSaveButton.IsEnabled=!sqsBusy;SqsTestButton.IsEnabled=!_sqsSaving;
        foreach(var control in new Control[]{CapacityAuto,SaveCapacityButton,ThumbnailToggle,RemoteAuto,IncrementalField,FullField,SaveIntervalsButton})control.IsEnabled=!sqsBusy;
        if(mount is null||_savingSwitch)return;
        var settings=mount.Definition;var capacity=_model.CapacitySettingsDraft(settings);var detection=_model.Draft(settings);_loading=true;
        try
        {
            AutoConnectField.IsOn=settings.AutoConnect;
            CapacityAuto.Visibility=_kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;CapacityAuto.IsOn=capacity.Automatic;
            MaximumField.Text=capacity.Text;CapacityUnit.SelectedIndex=capacity.Unit=="TB"?1:0;
            MaximumField.IsEnabled=CapacityUnit.IsEnabled=!capacity.Automatic&&!sqsBusy;
            CapacityDescription.Text=MountsViewModel.CapacitySummary(mount);RecheckButton.IsEnabled=!sqsBusy&&mount.IsConnected&&CapacityRules.Enabled(settings);
            ThumbnailToggle.Visibility=_model.CanConfigureThumbnails?Visibility.Visible:Visibility.Collapsed;ThumbnailToggle.IsOn=_model.ThumbnailDraft(settings);ThumbnailDescription.Text=_model.CanConfigureThumbnails?mount.ThumbnailDisplay:"";
            RemoteAuto.IsOn=detection.Enabled;IncrementalField.Value=detection.Incremental;FullField.Value=detection.Full;
            IncrementalField.Visibility=_kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;
            SqsEnabled.IsOn=settings.Sqs?.Enabled==true;DetectionDescription.Text=MountsViewModel.DetectionSummary(mount);SqsDescription.Text=mount.SqsDisplay;

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
        return new(ThumbnailToggle.IsOn,RemoteAuto.IsOn,checked((int)incremental),checked((int)full),CapacityAuto.IsOn,CapacityAuto.IsOn?null:CapacityRules.ParseMaximum(MaximumField.Text,CapacityUnit.SelectedIndex==1?"TB":"GB"),SqsOptions(),SqsKeys());
    }
    private async void SqsEnabledChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetSqsEnabledAsync(mount.Id,SqsEnabled.IsOn));}
    private async void ChooseFolder(object sender,RoutedEventArgs args)
    {
        if(Stopped||_submitting||_picking||_testing is not null||_sqsProbe is not null||_sqsSaving)return;_picking=true;Refresh();
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
        if(_submitting||_picking||_sqsProbe is not null||_sqsSaving)return;
        if(!ValidateFields(false))return;using var cancel=new CancellationTokenSource();_testing=cancel;Refresh();SecondaryButtonText="取消测试";
        ErrorBar.IsOpen=false;ProbeBar.Title="只读连接测试";ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.Message="正在执行只读探测…";ProbeBar.IsOpen=true;
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
        _fieldFeedback.Check(!string.IsNullOrWhiteSpace(BucketField.Text),BucketField,"请输入桶名。");_fieldFeedback.Check(!string.IsNullOrWhiteSpace(RegionField.Text),RegionField,"请输入服务实际使用的签名区域。");
        if(DefaultCredentialsField.IsChecked!=true){_fieldFeedback.Check(!string.IsNullOrWhiteSpace(AccessKeyField.Text),AccessKeyField,"请输入访问密钥 ID，或选择 SDK 默认凭据链。");_fieldFeedback.Check(SecretKeyField.Password.Length>0,SecretKeyField,"请输入访问密钥。");}
        return _fieldFeedback.Complete(this);
    }
    private async void SaveConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;if(Stopped||_submitting||_picking||_testing is not null||_sqsProbe is not null||_sqsSaving)return;
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
    {_testing?.Cancel();_sqsProbe?.Cancel();if(!_stopping&&(_submitting||_picking||_sqsSaving||(_existing is {} mount&&_model.IsBusy(mount.Id))))args.Cancel=true;}
    private void OnClosed(ContentDialog sender,ContentDialogClosedEventArgs args)=>Dispose();
    /// <summary>退出强制关闭视图，应用继续等待网络任务；迟到结果不更新已释放控件。</summary>
    public void CancelForShutdown(){if(_disposed)return;_stopping=true;_testing?.Cancel();_sqsProbe?.Cancel();Hide();Dispose();}
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_fieldFeedback.Dispose();_testing?.Cancel();_sqsProbe?.Cancel();ClearSecrets();
        _model.Refreshed-=Refresh;_model.StateChanged-=Refresh;
        EndpointField.TextChanged-=EndpointChanged;
        PrimaryButtonClick-=SaveConnection;SecondaryButtonClick-=TestConnection;Closing-=OnClosing;Closed-=OnClosed;
    }
    private void CredentialsModeChanged(object sender,RoutedEventArgs args){if(S3Secrets is not null)S3Secrets.IsEnabled=DefaultCredentialsField.IsChecked!=true;}
    private FileSourceConfiguration ReadSettings(string endpoint,bool allowHttp)=>new(FileSourceKind.S3,endpoint,BucketField.Text.Trim(),PrefixField.Text.Trim().Trim('/'),RegionField.Text.Trim(),PathStyleField.IsChecked==true,allowHttp,DefaultCredentialsField.IsChecked==true);
    private void EndpointChanged(object sender,TextChangedEventArgs args){if(!_loading)UpdateHttpWarning();}
    private SourceCredentials ReadCredentials()=>new(AccessKey:AccessKeyField.Text.Trim(),SecretKey:SecretKeyField.Password,SessionToken:SessionTokenField.Password);
    private void LoadProtocol(MountDefinition definition,SourceCredentials credentials,bool rootEditable)
    {
        var source=definition.Source!;BucketField.Text=source.Bucket;BucketField.IsReadOnly=!rootEditable;PrefixField.Text=source.Prefix;PrefixField.IsReadOnly=!rootEditable;
        RegionField.Text=source.Region;PathStyleField.IsChecked=source.PathStyle;DefaultCredentialsField.IsChecked=source.UseDefaultCredentials;
        AccessKeyField.Text=credentials.AccessKey;SecretKeyField.Password=credentials.SecretKey;SessionTokenField.Password=credentials.SessionToken;
    }
    /// <summary>只有用户改变地址才更新明文警告，后台刷新不会重开已关闭提示。</summary>
    private void UpdateHttpWarning(){var current=EndpointField.Text.Trim();if(_httpWarningContext==current)return;_httpWarningContext=current;HttpWarning.IsOpen=Uri.TryCreate(current,UriKind.Absolute,out var address)&&address.Scheme=="http";}
    private void ClearSecrets(){SecretKeyField.Password="";SessionTokenField.Password="";SqsSecretField.Password="";SqsSessionField.Password="";}
    private void LoadSqs(MountDefinition mount)
    {
        var options=mount.Sqs??new();SqsEnabled.IsOn=options.Enabled;SqsUrl.Text=options.QueueUrl;SqsRegion.Text=options.Region.Length>0?options.Region:mount.Source!.Region;SqsMode.SelectedIndex=(int)options.CredentialMode;
        var keys=_model.ReadSqsCredentials(mount.Id);SqsAccessField.Text=keys.AccessKey;SqsSecretField.Password=keys.SecretKey;SqsSessionField.Password=keys.SessionToken;
    }
    private void SqsCredentialsModeChanged(object sender,SelectionChangedEventArgs args){if(SqsSecrets is not null)SqsSecrets.IsEnabled=SqsMode.SelectedIndex==(int)SqsCredentialMode.SeparateKeys;}
    private SqsConfiguration SqsOptions()=>new(SqsEnabled.IsOn,SqsUrl.Text.Trim(),SqsRegion.Text.Trim(),(SqsCredentialMode)SqsMode.SelectedIndex);
    private SourceCredentials SqsKeys()=>new(AccessKey:SqsAccessField.Text.Trim(),SecretKey:SqsSecretField.Password,SessionToken:SqsSessionField.Password);
    /// <summary>同一配置窗口内只读测试队列；关闭或退出取消后，由应用等待并最终释放请求。</summary>
    private async void TestSqs(object sender,RoutedEventArgs args)
    {
        if(Stopped||_existing is null)return;
        if(_sqsProbe is {} running){running.Cancel();SqsTestButton.Content="取消中…";SqsTestButton.IsEnabled=false;return;}
        if(_submitting||_picking||_testing is not null||_sqsSaving)return;
        using var cancel=new CancellationTokenSource();_sqsProbe=cancel;Refresh();SqsTestButton.Content="取消测试";ProbeBar.Title="SQS 只读连接测试";ProbeBar.Message="正在读取队列属性…";ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.IsOpen=true;
        try{var result=await _model.TestSqsConnectionAsync(_existing.Id,SqsOptions(),SqsKeys(),cancel.Token);if(!Stopped){ProbeBar.Message=result.Message;ProbeBar.Severity=result.Success?InfoBarSeverity.Success:InfoBarSeverity.Error;}}
        catch(OperationCanceledException){if(!Stopped){ProbeBar.Message="SQS 测试已取消。";ProbeBar.Severity=InfoBarSeverity.Informational;}}
        catch(Exception error){if(!Stopped){ProbeBar.Message=error.Message;ProbeBar.Severity=InfoBarSeverity.Error;}}
        finally{_sqsProbe=null;if(!Stopped){SqsTestButton.Content="测试 SQS 连接（只读）";Refresh();}}
    }
    private async void SaveSqs(object sender,RoutedEventArgs args)
    {
        if(Stopped||_existing is null||_sqsSaving||_sqsProbe is not null||_testing is not null||_submitting||_picking)return;
        _sqsSaving=true;Refresh();
        try{await _model.SaveSqsConfigurationAsync(_existing.Id,SqsOptions(),SqsKeys());}
        finally{_sqsSaving=false;if(!Stopped)Refresh();}
    }
}
