using HybridMount.Models.UI;
using HybridMount;
using HybridMount.Commons.UI;
using HybridMount.Controls.Mounting;
using HybridMount.Infrastructure.Protocols.Synology;
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

/// <summary>群晖配置表单；UI 只提交应用拥有的异步操作，关闭时释放未交接的登录会话。</summary>
public sealed partial class SynologyConfigurationContentDialog : ContentDialog,IMountConfigurationDialog
{
    private IDisposable? _localization;
    private ProtocolCapabilityDialogBinding? _capabilityBinding;
    private ProtocolCapabilityProbeBinding? _probeBinding;
    private readonly HybridMount.UI.FormFieldFeedback _fieldFeedback=new();
    private MountsViewModel _model=null!;
    private readonly MountDefinition? _existing;
    private CancellationTokenSource? _testing;
    private bool _loading=true,_submitting,_picking,_disposed,_stopping,_rootEditable,_savingSwitch;
    private string? _pin,_certificateCandidate,_verified,_httpWarningContext;
    private string _deviceToken="";
    private IReadOnlyList<FileExclusionRule>? _importExclusions;
    private SynologyProbeResult? _probe;
    public MountsViewModel ViewModel=>_model;
    public MountDefinition? CreatedMount {get;private set;}
    public string? AcceptedTaskId {get;private set;}
    private CancellationTokenSource? _submission;
    public string ResultMessage {get;private set;}="";
    public PageMessageSeverity ResultSeverity {get;private set;}=PageMessageSeverity.Information;
    private bool Stopped=>_disposed||_stopping||_model.IsStopping;
    public SynologyConfigurationContentDialog(MountsViewModel model,MountDefinition? existing=null,MountBackupItem? imported=null)
    {
        InitializeComponent();_localization=global::HybridMount.UI.UserInterface.ObserveLanguage(this,()=>Bindings.Update());_model=model;_existing=existing;_rootEditable=existing is null;
        try
        {
            NameField.Text=existing?.Name??global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SynologyMount", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;EndpointField.Text=existing?.Source?.Endpoint??"https://:5001";
            var source=existing?.Source;_pin=source?.PinnedCertificateSha256;HttpField.IsChecked=source?.AllowHttp==true;
            if(existing is not null)
            {
                NameField.IsReadOnly=true;PathField.Text=existing.LocalPath;ChooseButton.IsEnabled=false;
                var credentials=model.ReadCredentials(existing.Id);UserField.Text=credentials.UserName;PasswordField.Password=credentials.Password;_deviceToken=credentials.SessionToken;
                var parts=source!.Prefix[1..].Split('/',2);ShareField.ItemsSource=new[]{new SynologySharedFolder(parts[0],"/"+parts[0])};ShareField.SelectedIndex=0;SubfolderField.Text=parts.Length>1?parts[1]:"";
                AutoConnectField.IsOn=existing.AutoConnect;
                RemoteAuto.IsOn=existing.RemoteAutoDetectEnabled;IncrementalField.Value=existing.RemoteIncrementalSeconds;FullField.Value=existing.RemoteFullSeconds;
                CapacityAuto.IsOn=existing.CapacityAutoDetectEnabled;MaximumField.Text=existing.MaximumCapacityBytes is {} maximum?((decimal)maximum/1_000_000_000m).ToString(System.Globalization.CultureInfo.CurrentCulture):"";
                ThumbnailToggle.IsOn=model.ThumbnailDraft(existing);ThumbnailToggle.IsEnabled=model.CanConfigureThumbnails;
                ExistingSettings.Visibility=SaveIntervalsButton.Visibility=Visibility.Visible;
            }
            if(imported is not null)
            {
                if(existing is not null||imported.Source.Kind!=FileSourceKind.Synology)throw new ArgumentException("导入协议与新建表单不匹配。");
                NameField.Text=imported.Name;PathField.Text=imported.LocalPath;EndpointField.Text=imported.Source.Endpoint;
                UserField.Text=imported.Credentials.UserName;PasswordField.Password=imported.Credentials.Password;_deviceToken="";
                _pin=imported.Source.PinnedCertificateSha256;HttpField.IsChecked=imported.Source.AllowHttp;
                var importedParts=imported.Source.Prefix.TrimStart('/').Split('/',2);
                if(importedParts[0].Length>0){ShareField.ItemsSource=new[]{new SynologySharedFolder(importedParts[0],"/"+importedParts[0])};ShareField.SelectedIndex=0;}
                SubfolderField.Text=importedParts.Length>1?importedParts[1]:"";
                AutoConnectField.IsOn=imported.AutoConnect;ThumbnailToggle.IsOn=imported.ThumbnailsEnabled;
                RemoteAuto.IsOn=imported.RemoteAutoDetectEnabled;IncrementalField.Value=imported.RemoteIncrementalSeconds;FullField.Value=imported.RemoteFullSeconds;
                CapacityAuto.IsOn=imported.CapacityAutoDetectEnabled;MaximumField.Text=imported.MaximumCapacityBytes is {} maximum?((decimal)maximum/1_000_000_000m).ToString(System.Globalization.CultureInfo.CurrentCulture):"";
                _importExclusions=ConnectionSubmissionDraft.FromBackup(imported).Exclusions;
            }
            ExistingSettings.Visibility=Visibility.Visible;ThumbnailToggle.IsEnabled=model.CanConfigureThumbnails;ThumbnailToggle.Visibility=model.CanConfigureThumbnails?Visibility.Visible:Visibility.Collapsed;SaveCapacityButton.Visibility=RecheckButton.Visibility=existing is null?Visibility.Collapsed:Visibility.Visible;
            global::HybridMount.UI.UserInterface.Set(this,"Title",existing is null ? global::HybridMount.Models.Localization.ResourceMessage.Create("NewSynologyNAS") : global::HybridMount.Models.Localization.ResourceMessage.Create("Settings", existing.Name));
            global::HybridMount.UI.UserInterface.Set(this, "PrimaryButtonText", existing is null ? global::HybridMount.Models.Localization.ResourceMessage.Create("CreateAndConnect") : global::HybridMount.Models.Localization.ResourceMessage.Create("SaveConnectionAndReconnect"));global::HybridMount.UI.UserInterface.Set(this, "SecondaryButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("TestConnection"));global::HybridMount.UI.UserInterface.Set(this, "CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("Close"));
            DefaultButton=existing is null?ContentDialogButton.Primary:ContentDialogButton.Close;
            PrimaryButtonClick+=SaveConnection;SecondaryButtonClick+=TestConnection;Closing+=OnClosing;Closed+=OnClosed;
            model.Refreshed+=Refresh;model.StateChanged+=Refresh;_capabilityBinding = new(this, CapabilityView, FileSourceKind.Synology, ()=>ProtocolCapabilityDialogBinding.Fingerprint(ReadSettings(false),ReadCredentials() with { SessionToken = "" }), ()=>_existing is {} saved ? _model.FindMount(saved.Id) : null, ()=>ProtocolCapabilityDialogBinding.Fingerprint(ReadSettings(false) with { Prefix = "" }, ReadCredentials() with { SessionToken = "" }));_probeBinding = new(this,CapabilityView,_capabilityBinding,model,()=>new(ReadSettings(true),ReadCredentials(),OneTimePassword:OtpField.Password),()=>ValidateFields(false),Refresh,message=>{global::HybridMount.UI.UserInterface.Set(ErrorBar, "Message", message);ErrorBar.IsOpen=true;});_loading=false;Refresh();UpdatePin();UpdateHttpWarning();
            if(existing is not null)_=LoadRemoteRootEditabilityAsync(existing.Id);
        }
        catch{Dispose();throw;}
    }
    private async Task LoadRemoteRootEditabilityAsync(Guid id)
    {
        var dispatcher=DispatcherQueue;
        try{var editable=await _model.CanEditRemoteRootAsync(id).ConfigureAwait(false);dispatcher.TryEnqueue(()=>{if(Stopped)return;_rootEditable=editable;Refresh();});}
        catch(Exception error){dispatcher.TryEnqueue(()=>{if(!Stopped)ShowError(error.Message);});}
    }
    private void Refresh()
    {
        _capabilityBinding?.Refresh();
        if(_disposed)return;if(_model.IsStopping){CancelForShutdown();return;}
        var mount=_existing is {} existing?_model.FindMount(existing.Id):null;if(_existing is not null&&mount is null){CancelForShutdown();return;}
        var busy=_probeBinding?.IsRunning==true||_savingSwitch||_submitting||_picking||_testing is not null||mount is not null&&!MountActionState.From(mount,_model.IsBusy(mount.Definition.Id)).CanEdit;
        _probeBinding?.Refresh(!busy);
        ConnectionFields.IsEnabled=SettingsFields.IsEnabled=!busy;IsPrimaryButtonEnabled=!busy;IsSecondaryButtonEnabled=!_submitting&&!_picking&&(_testing is not null||!busy);
        EndpointField.IsReadOnly=SubfolderField.IsReadOnly=!_rootEditable;ShareField.IsEnabled=HttpField.IsEnabled=_rootEditable;
        if(mount is not null&&!_savingSwitch){_loading=true;try{AutoConnectField.IsOn=mount.Definition.AutoConnect;RemoteAuto.IsOn=mount.Definition.RemoteAutoDetectEnabled;CapacityAuto.IsOn=mount.Definition.CapacityAutoDetectEnabled;ThumbnailToggle.IsOn=mount.Definition.ThumbnailsEnabled;}finally{_loading=false;}}
        if(mount is not null){DetectionDescription.Text=MountsViewModel.DetectionSummary(mount);CapacityDescription.Text=MountsViewModel.CapacitySummary(mount);}
        MaximumField.IsEnabled=!busy&&!CapacityAuto.IsOn;
    }
    private string Prefix
    {
        get
        {
            var root = ShareField.SelectedItem is SynologySharedFolder share ? share.Path : string.IsNullOrWhiteSpace(ShareField.Text) ? "" : "/" + ShareField.Text.Trim().Trim('/');
            return root.Length == 0 ? "" : root + (SubfolderField.Text.Length == 0 ? "" : "/" + SubfolderField.Text);
        }
    }
    private FileSourceConfiguration ReadSettings(bool requireShare)
    {
        if(requireShare&&Prefix.Length==0)throw new ArgumentException(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TestTheConnectionAndSelectASharedFolderFirst", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);
        var options=new FileSourceConfiguration(FileSourceKind.Synology,EndpointField.Text.Trim(),Prefix:Prefix,AllowHttp:HttpField.IsChecked==true,PinnedCertificateSha256:_pin);
        SynologyFileSource.ValidateOptions(options.Prefix.Length==0?options with{Prefix="/__probe__"}:options);return options;
    }
    private SourceCredentials ReadCredentials()=>new(UserField.Text.Trim(),PasswordField.Password,SessionToken:_deviceToken);
    private string Identity(FileSourceConfiguration options)=>options.Endpoint+"\0"+options.Prefix+"\0"+options.AllowHttp+"\0"+options.PinnedCertificateSha256;
    private void Invalidate(bool credentials)
    {
        if(_loading||_disposed)return;_verified=null;_certificateCandidate=null;CertificateBar.IsOpen=false;
        if(credentials){_deviceToken="";OtpField.Password="";_probe?.Session?.Discard();_probe=null;}
    }
    private void ClearShares()
    {if(!_rootEditable)return;_loading=true;try{ShareField.ItemsSource=null;SubfolderField.Text="";}finally{_loading=false;}}
    private void EndpointChanged(object sender,TextChangedEventArgs args){if(_loading||_disposed)return;Invalidate(true);ClearShares();_pin=null;UpdatePin();UpdateHttpWarning();}
    private void CredentialsChanged(object sender,TextChangedEventArgs args){if(_loading||_disposed)return;Invalidate(true);ClearShares();}
    private void PasswordChanged(object sender,RoutedEventArgs args)=>Invalidate(true);
    private void ConnectionChanged(object sender,RoutedEventArgs args)=>Invalidate(false);
    private void SubfolderChanged(object sender,TextChangedEventArgs args)=>Invalidate(false);
    private void ShareChanged(object sender,SelectionChangedEventArgs args)=>Invalidate(false);
    /// <summary>仅地址变化发布HTTP提示，其他刷新保留用户关闭状态。</summary>
    private void UpdateHttpWarning()
    {
        var context=EndpointField.Text.Trim();if(context==_httpWarningContext)return;_httpWarningContext=context;
        HttpWarning.IsOpen=Uri.TryCreate(context,UriKind.Absolute,out var uri)&&uri.Scheme=="http";
    }
    private void UpdatePin(){global::HybridMount.UI.UserInterface.Set(PinText,"Text",_pin is null ? global::HybridMount.Models.Localization.ResourceMessage.Create("CertificateValidationSystemTrust") : global::HybridMount.Models.Localization.ResourceMessage.Create("PinnedCertificateSHA256ForThisMount", _pin));ClearPinButton.Visibility=_pin is null?Visibility.Collapsed:Visibility.Visible;}
    private void ClearPin(object sender,RoutedEventArgs args){if(Stopped)return;Invalidate(false);_pin=null;UpdatePin();}
    private async void TrustCertificate(object sender,RoutedEventArgs args)
    {if(Stopped||_testing is not null||_submitting||_certificateCandidate is null)return;_pin=_certificateCandidate;Invalidate(false);UpdatePin();await ProbeAsync();}
    private void ShowError(string message){global::HybridMount.UI.UserInterface.Set(ErrorBar, "Message", message);ErrorBar.IsOpen=true;}
    private async Task<bool> ProbeAsync()
    {
        if(Stopped)return false;
        using var cancel=new CancellationTokenSource();_testing=cancel;_verified=null;_certificateCandidate=null;CertificateBar.IsOpen=false;Refresh();if(Stopped){_testing=null;return false;}
        global::HybridMount.UI.UserInterface.Set(this,"SecondaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("CancelTest"));ErrorBar.IsOpen=false;ProbeBar.IsOpen=true;ProbeBar.Severity=InfoBarSeverity.Informational;global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("RunningReadOnlyProbes"));
        try
        {
            var settings=ReadSettings(false);_capabilityBinding?.BeginTest();var result=await _model.TestSynologyConnectionAsync(settings,ReadCredentials(),OtpField.Password,cancel.Token,_probe?.Session);
            if(Stopped){await result.DisposeAsync();return false;}
            if(_probe is not null)await _probe.DisposeAsync();_probe=result;OtpField.Password="";global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", result.Message);ProbeBar.Severity=result.Success?InfoBarSeverity.Success:InfoBarSeverity.Error;
            if(result.CertificateSha256 is {} fingerprint){_certificateCandidate=fingerprint;global::HybridMount.UI.UserInterface.Set(CertificateBar, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("VerifyTheFollowingSHA256ThroughATrustedNASAdministrationChannelSystem", fingerprint));CertificateBar.IsOpen=true;}
            if(result.AuthenticationCode is 403 or 404 or 406){OtpField.Visibility=Visibility.Visible;OtpField.Focus(FocusState.Programmatic);}
            if(result.Credentials is null){_capabilityBinding?.CompleteTest(result.Capabilities);return false;}
            _deviceToken=result.Credentials.SessionToken;
            var selected=ShareField.SelectedItem as SynologySharedFolder;
            _loading=true;
            try{ShareField.ItemsSource=result.Shares;ShareField.SelectedItem=result.Shares.FirstOrDefault(x=>x.Path==selected?.Path);if(ShareField.SelectedItem is null&&_rootEditable&&result.Shares.Count>0)ShareField.SelectedIndex=0;}
            finally{_loading=false;}
            _capabilityBinding?.CompleteTest(result.Capabilities, serverDiscovery: settings.Prefix.Length == 0);
            if(!result.Success)return false;
            CertificateBar.IsOpen=false;_certificateCandidate=null;
            _verified=settings.Prefix.Length!=0&&settings.Prefix==Prefix?Identity(settings):null;
            if(result.Shares.Count==0){ShowError(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ThisAccountHasNoAccessibleSharedFoldersSoAMountCannotBe", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);return false;}
            return true;
        }
        catch(OperationCanceledException){_capabilityBinding?.CancelTest();if(!Stopped)global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("TheConnectionTestWasCanceled"));return false;}
        catch(Exception error){if(!Stopped){ProbeBar.Severity=InfoBarSeverity.Error;global::HybridMount.UI.UserInterface.Set(ProbeBar, "Message", error.Message);}return false;}
        finally{_testing=null;if(!Stopped){OtpField.Password="";global::HybridMount.UI.UserInterface.Set(this, "SecondaryButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("TestConnection"));Refresh();}}
    }
    private async void TestConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {args.Cancel=true;if(Stopped)return;if(_testing is {} testing){testing.Cancel();return;}if(!_submitting&&!_picking&&ValidateFields(false))await ProbeAsync();}
    private static int Seconds(double value,int minimum,int maximum)
    {if(!double.IsFinite(value)||value!=Math.Truncate(value)||value<minimum||value>maximum)throw new ArgumentException(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("EnterAWholeNumberDetectionIntervalWithinTheAllowedRange", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);return checked((int)value);}
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
            if(!CapacityAuto.IsOn)try{_=CapacityRules.ParseMaximum(MaximumField.Text,"GB");}catch(ArgumentException error){_fieldFeedback.Check(false,MaximumField,error.Message);}
        }
        _fieldFeedback.Check(!string.IsNullOrWhiteSpace(UserField.Text),UserField,global::HybridMount.Models.Localization.ResourceMessage.Create("EnterTheSynologyAccount"));_fieldFeedback.Check(PasswordField.Password.Length>0||_deviceToken.Length>0,PasswordField,global::HybridMount.Models.Localization.ResourceMessage.Create("EnterAPassword"));
        if(saving)_fieldFeedback.Check(Prefix.Length>0,ShareField,global::HybridMount.Models.Localization.ResourceMessage.Create("TestTheConnectionAndSelectASharedFolderFirst"));
        return _fieldFeedback.Complete(this);
    }
    private ConnectionSubmissionDraft CaptureSubmission()=>new(_existing?.Id,NameField.Text,PathField.Text,AutoConnectField.IsOn,ReadSettings(true),ReadCredentials(),
        _importExclusions??_model.ReadExclusionRules(_existing?.Id,FileSourceKind.Synology),new(ThumbnailToggle.IsOn,RemoteAuto.IsOn,Seconds(IncrementalField.Value,10,86400),Seconds(FullField.Value,30,604800),CapacityAuto.IsOn,CapacityAuto.IsOn?null:CapacityRules.ParseMaximum(MaximumField.Text,"GB")));
    /// <summary>本次只读认证完成且应用持久接纳后关闭；配置、连接和同步继续由应用拥有。</summary>
    private async void SaveConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;if(Stopped||_probeBinding?.IsRunning==true||_submitting||_picking||_testing is not null)return;
        if(!ValidateFields(true))return;var deferral=args.GetDeferral();using var cancel=new CancellationTokenSource();_submission=cancel;_submitting=true;Refresh();ErrorBar.IsOpen=false;
        try
        {
            var draft=CaptureSubmission();var otp=OtpField.Password;
            using var guard=new global::HybridMount.UI.SubmissionInputGuard(this,()=>cancel.Cancel());
            await using var proof=await _model.AuthenticateSubmissionAsync(draft,otp,cancel.Token);
            if(Stopped||!draft.SameInput(CaptureSubmission())){cancel.Cancel();return;}
            cancel.Token.ThrowIfCancellationRequested();var accepted=await _model.AcceptSubmissionAsync(proof,cancel.Token);
            AcceptedTaskId=accepted.TaskId;ResultMessage=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ConfigurationAcceptedAsTask",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
            ResultSeverity=PageMessageSeverity.Information;args.Cancel=false;
        }
        catch(ConnectionSubmissionException error)
        {
            if(Stopped)return;if(error.Fingerprint is {} fingerprint){_certificateCandidate=fingerprint;global::HybridMount.UI.UserInterface.Set(CertificateBar,"Message",global::HybridMount.Models.Localization.ResourceMessage.Create("VerifyTheFollowingSHA256ThroughATrustedNASAdministrationChannelSystem",fingerprint));CertificateBar.IsOpen=true;}
            if(error.AuthenticationCode is 403 or 404 or 406){OtpField.Visibility=Visibility.Visible;OtpField.Focus(FocusState.Programmatic);}
            global::HybridMount.UI.UserInterface.Set(ErrorBar,"Message",error.Message);ErrorBar.IsOpen=true;
        }
        catch(OperationCanceledException)
        {
            if(!Stopped){global::HybridMount.UI.UserInterface.Set(ErrorBar,"Message",global::HybridMount.Models.Localization.ResourceMessage.Create("ConfigurationSubmissionCanceled"));ErrorBar.IsOpen=true;}
        }
        catch(Exception error){if(!Stopped){global::HybridMount.UI.UserInterface.Set(ErrorBar,"Message",error.Message);ErrorBar.IsOpen=true;}}
        finally{_submission=null;_submitting=false;OtpField.Password="";if(!Stopped)Refresh();deferral.Complete();}
    }

    private async void SaveIntervals(object sender,RoutedEventArgs args)
    {if(Stopped||_existing is null)return;try{_model.UpdateDraft(_existing.Id,RemoteAuto.IsOn,Seconds(IncrementalField.Value,10,86400),Seconds(FullField.Value,30,604800));await _model.SaveDetectionAsync(_existing.Id);}catch(Exception error){if(!Stopped)ShowError(error.Message);}}
    private async void SaveCapacity(object sender,RoutedEventArgs args)
    {if(Stopped||_existing is null)return;_model.UpdateCapacityDraft(_existing.Id,CapacityAuto.IsOn,MaximumField.Text,"GB");await _model.SaveCapacityAsync(_existing.Id);}
    private async void RecheckCapacity(object sender,RoutedEventArgs args){if(!Stopped&&_existing is {} mount)await _model.RecheckCapacityAsync(mount.Id);}
    private async void SaveThumbnails(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetThumbnailAsync(mount.Id,ThumbnailToggle.IsOn));}
    /// <summary>即时开关保持候选状态直到应用完成保存，最后用正式值回滚故障；连接草稿独立保留。</summary>
    private async Task SaveSwitchAsync(Func<Task> save){if(Stopped||_savingSwitch)return;_savingSwitch=true;Refresh();try{await save();}finally{_savingSwitch=false;if(!Stopped)Refresh();}}
    private async void AutoConnectChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetAutoConnectAsync(mount.Id,AutoConnectField.IsOn));}
    private async void RemoteAutoChanged(object sender,RoutedEventArgs args){if(!_loading&&!Stopped&&_existing is {} mount)await SaveSwitchAsync(()=>_model.SetRemoteDetectionEnabledAsync(mount.Id,RemoteAuto.IsOn));}
    private async void CapacityAutoChanged(object sender,RoutedEventArgs args){if(_loading||Stopped)return;if(_existing is {} mount)await SaveSwitchAsync(()=>_model.SetCapacityAutomaticAsync(mount.Id,CapacityAuto.IsOn));else MaximumField.IsEnabled=!CapacityAuto.IsOn;}
    private async void ChooseFolder(object sender,RoutedEventArgs args)
    {
        if(Stopped||_probeBinding?.IsRunning==true||_submitting||_picking||_testing is not null)return;_picking=true;Refresh();
        try{if(Stopped)return;var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);var folder=await picker.PickSingleFolderAsync();if(!Stopped&&folder is not null)PathField.Text=folder.Path;}
        catch(Exception error){if(!Stopped)ShowError(error.Message);}finally{_picking=false;if(!Stopped)Refresh();}
    }
    private void OnClosing(ContentDialog sender,ContentDialogClosingEventArgs args)
    {_submission?.Cancel();_testing?.Cancel();if(!_stopping&&(_submitting||_picking||_existing is {} mount&&_model.IsBusy(mount.Id)))args.Cancel=true;}
    private void OnClosed(ContentDialog sender,ContentDialogClosedEventArgs args)=>Dispose();
    public void CancelForShutdown(){if(_disposed)return;_stopping=true;_submission?.Cancel();_testing?.Cancel();Hide();Dispose();}
    public void Dispose()
    {_probeBinding?.Dispose();_capabilityBinding?.Dispose();_localization?.Dispose();_localization=null;
        if(_disposed)return;_disposed=true;_importExclusions=null;_submission?.Cancel();_fieldFeedback.Dispose();_testing?.Cancel();PasswordField.Password=OtpField.Password="";_deviceToken="";_probe?.Session?.Discard();_probe=null;
        _model.Refreshed-=Refresh;_model.StateChanged-=Refresh;PrimaryButtonClick-=SaveConnection;SecondaryButtonClick-=TestConnection;Closing-=OnClosing;Closed-=OnClosed;
    }
}
