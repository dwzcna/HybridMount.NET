using HybridMount.Models.UI;
using HybridMount.Models.Protocols.S3;
using HybridMount;
using HybridMount.Commons.UI;
using HybridMount.Controls.Mounting;
using HybridMount.Infrastructure.Protocols.S3;
using HybridMount.Models.Configuration;
using HybridMount.Models.Localization;
using HybridMount.Models.Mounting;
using HybridMount.UI;
using HybridMount.UI.Localization;
using HybridMount.UI.Presentation;
using HybridMount.ViewModels;
using HybridMount.Views.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Storage.Pickers;
namespace HybridMount.Views.Dialogs.Mounting;

/// <summary>S3 完整配置对话框；连接、凭据、容量及检测控件在同一 XAML 中，业务由唯一挂载模型拥有。</summary>
public sealed partial class S3ConfigurationContentDialog : ContentDialog,IMountConfigurationDialog
{
    private IDisposable? _localization;
    public ProtocolInteractionViewModel Interaction { get; } = new();
    /// <summary>所属窗口的目录选择适配；返回null代表取消，不触发业务验证或远端请求。</summary>
    internal Func<Task<string?>> SelectFolderAsync { get; set; } = async () =>
    {
        var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
        return (await picker.PickSingleFolderAsync())?.Path;
    };
    private ProtocolCapabilityDialogBinding? _capabilityBinding;
    private ProtocolCapabilityProbeBinding? _probeBinding;
    private readonly HybridMount.UI.FormFieldFeedback _fieldFeedback=new();
    private MountsViewModel _model=null!;
    private MountDefinition? _existing;
    private FileSourceKind _kind;
    private Guid? _importExclusionScope;
    private Guid? ExclusionOwner=>_importExclusionScope??_existing?.Id;
    private CancellationTokenSource? _testing,_sqsProbe;
    private bool _sqsSaving;
    private bool _loading=true,_submitting,_picking,_disposed,_stopping,_savingSwitch;
    private string? _httpWarningContext;
    public MountsViewModel ViewModel=>_model;
    public MountDefinition? CreatedMount {get;private set;}
    public string? AcceptedTaskId {get;private set;}
    private CancellationTokenSource? _submission;
    public string ResultMessage {get;private set;}="";
    public PageMessageSeverity ResultSeverity {get;private set;}=PageMessageSeverity.Information;
    private bool Stopped=>_disposed||_stopping||_model.IsStopping;
    /// <summary>只加载一次连接草稿；后续刷新仅更新已保存设置，不覆盖正在编辑的账号或地址。</summary>
    public S3ConfigurationContentDialog(MountsViewModel model,MountDefinition? existing=null,MountBackupItem? imported=null)
    {
        InitializeComponent();_localization=global::HybridMount.UI.UserInterface.ObserveLanguage(this,()=>Bindings.Update());_model=model;_kind=FileSourceKind.S3;_existing=existing;
        Interaction.SetHandler(ExecuteInteractionAsync, error =>
        {
            if (!Stopped) { global::HybridMount.UI.UserInterface.Set(ErrorBar, "Message", error.Message); ErrorBar.IsOpen = true; }
        });
        try
        {
            NameField.Text=existing?.Name??(_kind==FileSourceKind.S3?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("S3Mount", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("WebDAVMount", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);
            EndpointField.PlaceholderText=_kind==FileSourceKind.S3?"https://s3.example.com":"https://dav.example.com/dav/";
            if(existing is not null)
            {
                var editable=false;var source=existing.Source!;
                NameField.IsReadOnly=true;PathField.Text=existing.LocalPath;
                EndpointField.Text=source.Endpoint;EndpointField.IsReadOnly=!editable;HttpField.IsChecked=source.AllowHttp;
                AutoConnectField.IsOn=existing.AutoConnect;
                LoadProtocol(existing,model.ReadCredentials(existing.Id),editable);LoadSqs(existing);SettingsFields.Visibility=Visibility.Visible;
            }
            SettingsFields.Visibility=Visibility.Visible;ThumbnailToggle.Visibility=model.CanConfigureThumbnails?Visibility.Visible:Visibility.Collapsed;
            SaveCapacityButton.Visibility=RecheckButton.Visibility=SaveIntervalsButton.Visibility=existing is null?Visibility.Collapsed:Visibility.Visible;
            if(existing is null){RemoteAuto.IsOn=true;IncrementalField.Value=60;FullField.Value=300;CapacityUnit.SelectedIndex=0;}
            CapacityAuto.Visibility=Visibility.Collapsed;if(existing is null){SqsMode.SelectedIndex=0;SqsTestButton.Visibility=SqsSaveButton.Visibility=Visibility.Collapsed;}
            EndpointField.TextChanged+=EndpointChanged;
            if(imported is not null)
            {
                if(existing is not null||imported.Source.Kind!=_kind)throw new ArgumentException("导入协议与新建表单不匹配。");
                _importExclusionScope=model.CreateImportExclusions(_kind,imported.ExclusionRules);
                LoadImported(imported);
            }
            ExclusionList.ItemsSource=model.ExclusionDraft(ExclusionOwner,_kind);
            SaveExclusionsButton.Visibility=existing is null?Visibility.Collapsed:Visibility.Visible;
            global::HybridMount.UI.UserInterface.Set(this,"Title",global::HybridMount.Models.Localization.ResourceMessage.Create("JoinedText", existing is null ? global::HybridMount.Models.Localization.ResourceMessage.Create("Add") : global::HybridMount.Models.Localization.ResourceMessage.Create("SettingsDetails"), (existing?.Name??(_kind==FileSourceKind.S3?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("S3Mount", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("WebDAVMount", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!))));
            global::HybridMount.UI.UserInterface.Set(this, "PrimaryButtonText", existing is null ? global::HybridMount.Models.Localization.ResourceMessage.Create("CreateAndConnect") : global::HybridMount.Models.Localization.ResourceMessage.Create("SaveConnectionAndReconnect"));global::HybridMount.UI.UserInterface.Set(this, "SecondaryButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("TestConnection"));global::HybridMount.UI.UserInterface.Set(this, "CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("Close"));
            DefaultButton=existing is null?ContentDialogButton.Primary:ContentDialogButton.Close;
            PrimaryButtonClick+=SaveConnection;SecondaryButtonClick+=TestConnection;Closing+=OnClosing;Closed+=OnClosed;
            model.Refreshed+=Refresh;model.StateChanged+=Refresh;_capabilityBinding = new(this, CapabilityView, FileSourceKind.S3, ()=>ProtocolCapabilityDialogBinding.Fingerprint(ReadSettings(EndpointField.Text.Trim(),HttpField.IsChecked==true),ReadCredentials(),new { Options = SqsOptions(), Keys = SqsKeys() }), ()=>_existing is {} saved ? _model.FindMount(saved.Id) : null);_probeBinding = new(this,CapabilityView,_capabilityBinding,model,()=>new(ReadSettings(EndpointField.Text.Trim(),HttpField.IsChecked==true),ReadCredentials(),SqsOptions(),SqsKeys()),()=>ValidateFields(false),Refresh,message=>{global::HybridMount.UI.UserInterface.Set(ErrorBar, "Message", message);ErrorBar.IsOpen=true;});_loading=false;Refresh();UpdateHttpWarning();
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
            dispatcher.TryEnqueue(()=>{if(Stopped)return;EndpointField.IsReadOnly=!editable;BucketField.IsReadOnly=!editable;PrefixField.IsReadOnly=!editable;PublishInteraction();});
        }
        catch(Exception error){dispatcher.TryEnqueue(()=>{if(Stopped)return;global::HybridMount.UI.UserInterface.Set(ErrorBar, "Message", error.Message);ErrorBar.IsOpen=true;});}
    }
    /// <summary>命令与原生窗口操作之间的薄适配；返回完整Task，由交互模型持有。</summary>
    private Task ExecuteInteractionAsync(string action, object? parameter)
    {
        switch (action)
        {
            case "ChooseFolder": return ChooseFolderAsync();
            case "AddExclusionRule": AddExclusionRuleCore(); return Task.CompletedTask;
            case "AddDefaultExclusionRules": AddDefaultExclusionRulesCore(); return Task.CompletedTask;
            case "SaveExclusions": return SaveExclusionsAsync();
            case "SaveCapacity": return SaveCapacityAsync();
            case "RecheckCapacity": return RecheckCapacityAsync();
            case "SaveIntervals": return SaveIntervalsAsync();
            case "TestSqs": return TestSqsAsync();
            case "SaveSqs": return SaveSqsAsync();
            default: return Task.CompletedTask;
        }
    }
    /// <summary>只读取当前草稿和已发布快照；UI线程即时更新可用性，选择器往返不调用此方法。</summary>
    private void PublishInteraction()
    {
        if (_model is null) return;
        var mount = _existing is { } saved ? _model.FindMount(saved.Id) : null;
        Interaction.Publish(new ProtocolInteractionState(
            Stopped: Stopped, Existing: _existing is not null,
            SavedBusy: _savingSwitch || mount is not null && !MountActionState.From(mount, _model.IsBusy(mount.Definition.Id)).CanEdit,
            Submitting: _submitting, Testing: _testing is not null, TestCanceling: _testing?.IsCancellationRequested == true,
            Probing: _probeBinding?.IsRunning == true, CapacityAutomatic: CapacityAuto.IsOn,
            CapacityAvailable: mount?.IsConnected == true && CapacityRules.Enabled(mount.Definition),
            RootEditable: !EndpointField.IsReadOnly, SqsTesting: _sqsProbe is not null, SqsCanceling: _sqsProbe?.IsCancellationRequested == true, SqsSaving: _sqsSaving, SeparateSqsKeys: SqsMode.SelectedIndex == (int)SqsCredentialMode.SeparateKeys, UseDefaultCredentials: DefaultCredentialsField.IsChecked == true,
            ThumbnailsAvailable: _model.CanConfigureThumbnails));
        _probeBinding?.Refresh(Interaction.CanSubmit);
    }
    private void Refresh()
    {
        if(_disposed)return;
        if(_model.IsStopping){CancelForShutdown();return;}
        var mount=_existing is {} definition?_model.FindMount(definition.Id):null;
        if(_existing is not null&&mount is null){CancelForShutdown();return;}
        PublishInteraction();
        _capabilityBinding?.Refresh();
        if(mount is null||_savingSwitch)return;
        var settings=mount.Definition;var capacity=_model.CapacitySettingsDraft(settings);var detection=_model.Draft(settings);_loading=true;
        try
        {
            AutoConnectField.IsOn=settings.AutoConnect;
            CapacityAuto.Visibility=_kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;CapacityAuto.IsOn=capacity.Automatic;
            MaximumField.Text=capacity.Text;CapacityUnit.SelectedIndex=capacity.Unit=="TB"?1:0;

            CapacityDescription.Text=MountsViewModel.CapacitySummary(mount);
            ThumbnailToggle.Visibility=_model.CanConfigureThumbnails?Visibility.Visible:Visibility.Collapsed;ThumbnailToggle.IsOn=_model.ThumbnailDraft(settings);ThumbnailDescription.Text=_model.CanConfigureThumbnails?mount.ThumbnailDisplay:"";
            RemoteAuto.IsOn=detection.Enabled;IncrementalField.Value=detection.Incremental;FullField.Value=detection.Full;
            IncrementalField.Visibility=_kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;
            SqsEnabled.IsOn=settings.Sqs?.Enabled==true;DetectionDescription.Text=MountsViewModel.DetectionSummary(mount);SqsDescription.Text=mount.SqsDisplay;

        }
        finally{_loading=false;PublishInteraction();}
    }
    private void AddExclusionRuleCore(){if(!Stopped)_model.AddExclusionRule(ExclusionOwner,_kind);}
    private void AddDefaultExclusionRulesCore(){if(!Stopped)_model.AddDefaultExclusionRules(ExclusionOwner,_kind);}
    private void RemoveExclusionRule(object sender,RoutedEventArgs args){if(!Stopped&&Interaction.CanEditSettings&&sender is FrameworkElement{Tag:FileExclusionRuleViewModel row})_model.RemoveExclusionRule(ExclusionOwner,_kind,row);}
    private async Task SaveExclusionsAsync(){if(!Stopped&&_existing is {} mount)await _model.SaveExclusionsAsync(mount.Id);}
    private void CaptureCapacity(){if(!_loading&&!Stopped&&_existing is {} mount)_model.UpdateCapacityDraft(mount.Id,CapacityAuto.IsOn,MaximumField.Text,CapacityUnit.SelectedIndex==1?"TB":"GB");}
    private void CaptureDetection(){if(!_loading&&!Stopped&&_existing is {} mount)_model.UpdateDraft(mount.Id,RemoteAuto.IsOn,IncrementalField.Value,FullField.Value);}
    private void CapacityTextChanged(object sender,TextChangedEventArgs args)=>CaptureCapacity();
    private void CapacityUnitChanged(object sender,SelectionChangedEventArgs args)=>CaptureCapacity();
    private async void CapacityModeChanged(object sender,RoutedEventArgs args){if(_loading||Stopped)return;if(_existing is {} mount)await SaveSwitchAsync(()=>_model.SetCapacityAutomaticAsync(mount.Id,CapacityAuto.IsOn));else PublishInteraction();}
    private void IntervalChanged(NumberBox sender,NumberBoxValueChangedEventArgs args)=>CaptureDetection();
    private async Task SaveCapacityAsync(){if(Stopped||_existing is null)return;CaptureCapacity();await _model.SaveCapacityAsync(_existing.Id);}
    private async Task RecheckCapacityAsync(){if(!Stopped&&_existing is {} mount)await _model.RecheckCapacityAsync(mount.Id);}
    private async Task SaveIntervalsAsync(){if(Stopped||_existing is null)return;CaptureDetection();await _model.SaveDetectionAsync(_existing.Id);}
    private async void RemoteAutoChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetRemoteDetectionEnabledAsync(mount.Id,RemoteAuto.IsOn));}
    private async void ThumbnailChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetThumbnailAsync(mount.Id,ThumbnailToggle.IsOn));}
    /// <summary>开关提交期间保留候选值并禁用交互，结束后统一重读正式快照；失败时回滚而不覆盖其他草稿。</summary>
    private async Task SaveSwitchAsync(Func<Task> save)
    {if(Stopped||_savingSwitch)return;_savingSwitch=true;Refresh();try{await save();}finally{_savingSwitch=false;if(!Stopped)Refresh();}}
    private async void AutoConnectChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetAutoConnectAsync(mount.Id,AutoConnectField.IsOn));}
    private MountCreationOptions CreationOptions()
    {
        var incremental=IncrementalField.Value;var full=FullField.Value;
        if(!double.IsFinite(incremental)||incremental!=Math.Truncate(incremental)||!double.IsFinite(full)||full!=Math.Truncate(full))throw new ArgumentException(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("DetectionIntervalsMustBeValidWholeNumbersOfSeconds", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);
        return new(ThumbnailToggle.IsOn,RemoteAuto.IsOn,checked((int)incremental),checked((int)full),CapacityAuto.IsOn,CapacityAuto.IsOn?null:CapacityRules.ParseMaximum(MaximumField.Text,CapacityUnit.SelectedIndex==1?"TB":"GB"),SqsOptions(),SqsKeys());
    }
    private async void SqsEnabledChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetSqsEnabledAsync(mount.Id,SqsEnabled.IsOn));}
    private async Task ChooseFolderAsync()
    {
        if(Stopped||_submitting||_picking||_existing is not null)return;_picking=true;
        try
        {
            var path=await SelectFolderAsync();if(!Stopped&&path is not null)PathField.Text=path;
        }
        catch(Exception error){if(!Stopped){global::HybridMount.UI.UserInterface.Set(ErrorBar, "Message", error.Message);ErrorBar.IsOpen=true;}}
        finally{_picking=false;}
    }
    /// <summary>只读测试可由同一按钮取消；正文和请求由应用拥有，关闭只取消、不提前释放令牌。</summary>
    private async void TestConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;if(Stopped)return;
        if(_testing is {} testing){global::HybridMount.UI.UserInterface.Set(this,"SecondaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("Canceling"));testing.Cancel();PublishInteraction();return;}
        if(_submitting||_picking||_sqsProbe is not null||_sqsSaving)return;
        if(!ValidateFields(false))return;using var cancel=new CancellationTokenSource();_testing=cancel;Refresh();global::HybridMount.UI.UserInterface.Set(this,"SecondaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("CancelTest"));
        ErrorBar.IsOpen=false;global::HybridMount.UI.UserInterface.Set(ProbeBar,"Title",global::HybridMount.Models.Localization.ResourceMessage.Create("ReadOnlyConnectionTest"));ProbeBar.Severity=InfoBarSeverity.Informational;global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("RunningReadOnlyProbes"));ProbeBar.IsOpen=true;
        try
        {
            _capabilityBinding?.BeginTest();
            var result=await _model.TestConnectionAsync(ReadSettings(EndpointField.Text.Trim(),HttpField.IsChecked==true),ReadCredentials(),cancel.Token);
            if(!Stopped){_capabilityBinding?.CompleteTest(result.Capabilities);ProbeBar.Severity=result.Success?InfoBarSeverity.Success:InfoBarSeverity.Error;global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", result.Message);}
        }
        catch(OperationCanceledException){_capabilityBinding?.CancelTest();if(!Stopped){ProbeBar.Severity=InfoBarSeverity.Informational;global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("TheConnectionTestWasCanceled"));}}
        catch(Exception){if(!Stopped){_capabilityBinding?.CompleteTest(null);ProbeBar.Severity=InfoBarSeverity.Error;global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("ConnectionTestFailedCheckTheConfigurationNetworkAndCredentialSource"));}}
        finally{_testing=null;if(!Stopped){global::HybridMount.UI.UserInterface.Set(this, "SecondaryButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("TestConnection"));Refresh();}}
    }
    /// <summary>在禁用控件或发起请求之前校验草稿，字段错误保持原输入并聚焦首个错误。</summary>
    private bool ValidateFields(bool saving)
    {
        _fieldFeedback.Begin();
        if(saving){_fieldFeedback.Check(!string.IsNullOrWhiteSpace(NameField.Text),NameField,global::HybridMount.Models.Localization.ResourceMessage.Create("MountNameRequired"));_fieldFeedback.Check(Path.IsPathFullyQualified(PathField.Text),PathField,global::HybridMount.Models.Localization.ResourceMessage.Create("LocalDirectoryRequired"));}
        var validAddress=Uri.TryCreate(EndpointField.Text.Trim(),UriKind.Absolute,out var address)&&address.Scheme is "https" or "http";
        _fieldFeedback.Check(validAddress,EndpointField,global::HybridMount.Models.Localization.ResourceMessage.Create("ServerAddressRequired"));
        if(validAddress)_fieldFeedback.Check(address!.Scheme=="https"||HttpField.IsChecked==true,EndpointField,global::HybridMount.Models.Localization.ResourceMessage.Create("UnencryptedHTTPRequiresExplicitPermissionHTTPSIsRecommended"));
        if(saving&&_existing is null)
        {
            _fieldFeedback.Check(double.IsFinite(IncrementalField.Value)&&IncrementalField.Value==Math.Truncate(IncrementalField.Value)&&IncrementalField.Value is >=10 and <=86400,IncrementalField,global::HybridMount.Models.Localization.ResourceMessage.Create("EnterAWholeNumberOfSecondsFrom10To86400"));
            _fieldFeedback.Check(double.IsFinite(FullField.Value)&&FullField.Value==Math.Truncate(FullField.Value)&&FullField.Value is >=30 and <=604800,FullField,global::HybridMount.Models.Localization.ResourceMessage.Create("EnterAWholeNumberOfSecondsFrom30To604800"));
            if(!CapacityAuto.IsOn)try{_=CapacityRules.ParseMaximum(MaximumField.Text,CapacityUnit.SelectedIndex==1?"TB":"GB");}catch(ArgumentException error){_fieldFeedback.Check(false,MaximumField,error.Message);}
        }
        _fieldFeedback.Check(!string.IsNullOrWhiteSpace(BucketField.Text),BucketField,global::HybridMount.Models.Localization.ResourceMessage.Create("EnterABucketName"));_fieldFeedback.Check(!string.IsNullOrWhiteSpace(RegionField.Text),RegionField,global::HybridMount.Models.Localization.ResourceMessage.Create("EnterTheSigningRegionUsedByTheService"));
        if(DefaultCredentialsField.IsChecked!=true){_fieldFeedback.Check(!string.IsNullOrWhiteSpace(AccessKeyField.Text),AccessKeyField,global::HybridMount.Models.Localization.ResourceMessage.Create("EnterAnAccessKeyIDOrSelectTheDefaultSDKCredentialChain"));_fieldFeedback.Check(SecretKeyField.Password.Length>0,SecretKeyField,global::HybridMount.Models.Localization.ResourceMessage.Create("EnterTheAccessKey"));}
        return _fieldFeedback.Complete(this);
    }
    /// <summary>只预填独立新建草稿；加载期间不发布开关、认证、能力结果或配置写入。</summary>
    private void LoadImported(MountBackupItem item)
    {
        var draft=ConnectionSubmissionDraft.FromBackup(item);
        NameField.Text=draft.Name;PathField.Text=draft.Path;EndpointField.Text=draft.Source.Endpoint;
        HttpField.IsChecked=item.Source.AllowHttp;AutoConnectField.IsOn=item.AutoConnect;
        ThumbnailToggle.IsOn=item.ThumbnailsEnabled;RemoteAuto.IsOn=item.RemoteAutoDetectEnabled;
        IncrementalField.Value=item.RemoteIncrementalSeconds;FullField.Value=item.RemoteFullSeconds;
        CapacityAuto.IsOn=item.CapacityAutoDetectEnabled;CapacityUnit.SelectedIndex=0;
        MaximumField.Text=item.MaximumCapacityBytes is {} maximum?((decimal)maximum/1_000_000_000m).ToString(System.Globalization.CultureInfo.CurrentCulture):"";
        BucketField.Text=item.Source.Bucket;PrefixField.Text=item.Source.Prefix;RegionField.Text=item.Source.Region;
        PathStyleField.IsChecked=item.Source.PathStyle;DefaultCredentialsField.IsChecked=item.Source.UseDefaultCredentials;
        AccessKeyField.Text=item.Credentials.AccessKey;SecretKeyField.Password=item.Credentials.SecretKey;SessionTokenField.Password=item.Credentials.SessionToken;
        var sqs=item.Sqs??new();var keys=item.SqsCredentials??new();
        SqsEnabled.IsOn=sqs.Enabled;SqsUrl.Text=sqs.QueueUrl;SqsRegion.Text=sqs.Region;SqsMode.SelectedIndex=(int)sqs.CredentialMode;
        SqsAccessField.Text=keys.AccessKey;SqsSecretField.Password=keys.SecretKey;SqsSessionField.Password=keys.SessionToken;
    }
    private ConnectionSubmissionDraft CaptureSubmission()=>new(_existing?.Id,NameField.Text,PathField.Text,AutoConnectField.IsOn,ReadSettings(EndpointField.Text.Trim(),HttpField.IsChecked==true),ReadCredentials(),_model.ReadExclusionRules(ExclusionOwner,_kind),CreationOptions());
    /// <summary>本次只读认证完成且应用持久接纳后关闭；配置、连接和同步继续由应用拥有。</summary>
    private async void SaveConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;if(Stopped||_probeBinding?.IsRunning==true||_submitting||_picking||_testing is not null||_sqsProbe is not null||_sqsSaving)return;
        if(!ValidateFields(true))return;var deferral=args.GetDeferral();using var cancel=new CancellationTokenSource();_submission=cancel;_submitting=true;Refresh();ErrorBar.IsOpen=false;
        try
        {
            var draft=CaptureSubmission();string? otp=null;
            using var guard=new global::HybridMount.UI.SubmissionInputGuard(this,()=>cancel.Cancel());
            await using var proof=await _model.AuthenticateSubmissionAsync(draft,otp,cancel.Token);
            if(Stopped||!draft.SameInput(CaptureSubmission())){cancel.Cancel();return;}
            cancel.Token.ThrowIfCancellationRequested();var accepted=await _model.AcceptSubmissionAsync(proof,cancel.Token);
            AcceptedTaskId=accepted.TaskId;ResultMessage=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ConfigurationAcceptedAsTask",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
            ResultSeverity=PageMessageSeverity.Information;args.Cancel=false;
        }
        catch(ConnectionSubmissionException error)
        {
            if(Stopped)return;
            global::HybridMount.UI.UserInterface.Set(ErrorBar,"Message",error.Message);ErrorBar.IsOpen=true;
        }
        catch(OperationCanceledException)
        {
            if(!Stopped){global::HybridMount.UI.UserInterface.Set(ErrorBar,"Message",global::HybridMount.Models.Localization.ResourceMessage.Create("ConfigurationSubmissionCanceled"));ErrorBar.IsOpen=true;}
        }
        catch(Exception error){if(!Stopped){global::HybridMount.UI.UserInterface.Set(ErrorBar,"Message",error.Message);ErrorBar.IsOpen=true;}}
        finally{_submission=null;_submitting=false;if(!Stopped)Refresh();deferral.Complete();}
    }

    private void OnClosing(ContentDialog sender,ContentDialogClosingEventArgs args)
    {_submission?.Cancel();_testing?.Cancel();_sqsProbe?.Cancel();if(!_stopping&&(_submitting||_picking||_sqsSaving||(_existing is {} mount&&_model.IsBusy(mount.Id))))args.Cancel=true;}
    private void OnClosed(ContentDialog sender,ContentDialogClosedEventArgs args)=>Dispose();
    /// <summary>退出强制关闭视图，应用继续等待网络任务；迟到结果不更新已释放控件。</summary>
    public void CancelForShutdown(){if(_disposed)return;_stopping=true;_submission?.Cancel();_testing?.Cancel();_sqsProbe?.Cancel();Hide();Dispose();}
    public void Dispose()
    {_probeBinding?.Dispose();_capabilityBinding?.Dispose();_localization?.Dispose();_localization=null;
        if(_disposed)return;_disposed=true;Interaction.Dispose();if(_importExclusionScope is {} scope)_model.ReleaseImportExclusions(scope,_kind);_submission?.Cancel();_fieldFeedback.Dispose();_testing?.Cancel();_sqsProbe?.Cancel();ClearSecrets();
        _model.Refreshed-=Refresh;_model.StateChanged-=Refresh;
        EndpointField.TextChanged-=EndpointChanged;
        PrimaryButtonClick-=SaveConnection;SecondaryButtonClick-=TestConnection;Closing-=OnClosing;Closed-=OnClosed;
    }
    private void CredentialsModeChanged(object sender,RoutedEventArgs args){if(S3Secrets is not null)PublishInteraction();}
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
    private void SqsCredentialsModeChanged(object sender,SelectionChangedEventArgs args){if(SqsSecrets is not null)PublishInteraction();}
    private SqsConfiguration SqsOptions()=>new(SqsEnabled.IsOn,SqsUrl.Text.Trim(),SqsRegion.Text.Trim(),(SqsCredentialMode)SqsMode.SelectedIndex);
    private SourceCredentials SqsKeys()=>new(AccessKey:SqsAccessField.Text.Trim(),SecretKey:SqsSecretField.Password,SessionToken:SqsSessionField.Password);
    /// <summary>同一配置窗口内只读测试队列；关闭或退出取消后，由应用等待并最终释放请求。</summary>
    private async Task TestSqsAsync()
    {
        if(Stopped||_existing is null)return;
        if(_sqsProbe is {} running){running.Cancel();SqsTestButton.Content=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Canceling", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;PublishInteraction();return;}
        if(_submitting||_picking||_testing is not null||_sqsSaving)return;
        using var cancel=new CancellationTokenSource();_sqsProbe=cancel;Refresh();SqsTestButton.Content=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CancelTest", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;global::HybridMount.UI.UserInterface.Set(ProbeBar,"Title",global::HybridMount.Models.Localization.ResourceMessage.Create("ReadOnlySQSConnectionTest"));global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("ReadingQueueAttributes"));ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.IsOpen=true;
        try{_capabilityBinding?.BeginQueueTest();var result=await _model.TestSqsConnectionAsync(_existing.Id,SqsOptions(),SqsKeys(),cancel.Token);if(!Stopped){_capabilityBinding?.CompleteQueueTest(result.Capabilities);global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", result.Message);ProbeBar.Severity=result.Success?InfoBarSeverity.Success:InfoBarSeverity.Error;}}
        catch(OperationCanceledException){_capabilityBinding?.CancelTest();if(!Stopped){global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("TheSQSTestWasCanceled"));ProbeBar.Severity=InfoBarSeverity.Informational;}}
        catch(Exception error){if(!Stopped){global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", error.Message);ProbeBar.Severity=InfoBarSeverity.Error;}}
        finally{_sqsProbe=null;if(!Stopped){SqsTestButton.Content=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TestSQSConnectionReadOnly", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;Refresh();}}
    }
    private async Task SaveSqsAsync()
    {
        if(Stopped||_existing is null||_sqsSaving||_sqsProbe is not null||_testing is not null||_submitting||_picking)return;
        _sqsSaving=true;Refresh();
        try{await _model.SaveSqsConfigurationAsync(_existing.Id,SqsOptions(),SqsKeys());}
        finally{_sqsSaving=false;if(!Stopped)Refresh();}
    }
}
