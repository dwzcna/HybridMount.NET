using HybridMount.Models.Mounting.Synology;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Storage.Pickers;

namespace HybridMount.Controls.Mounting;

/// <summary>群晖配置表单；UI 只提交应用拥有的异步操作，关闭时释放未交接的登录会话。</summary>
public sealed partial class SynologyConfigurationContentDialog : ContentDialog,IMountConfigurationDialog
{
    private MountsViewModel _model=null!;
    private readonly MountDefinition? _existing;
    private CancellationTokenSource? _testing;
    private bool _loading=true,_submitting,_picking,_disposed,_stopping,_rootEditable;
    private string? _pin,_certificateCandidate,_verified;
    private string _deviceToken="";
    private SynologyProbeResult? _probe;
    public MountDefinition? CreatedMount {get;private set;}
    public string ResultMessage {get;private set;}="";
    private bool Stopped=>_disposed||_stopping||_model.IsStopping;
    public SynologyConfigurationContentDialog(MountsViewModel model,MountDefinition? existing=null)
    {
        InitializeComponent();_model=model;_existing=existing;_rootEditable=existing is null;
        try
        {
            NameField.Text=existing?.Name??"群晖挂载";EndpointField.Text=existing?.Source?.Endpoint??"https://:5001";
            var source=existing?.Source;_pin=source?.PinnedCertificateSha256;HttpField.IsChecked=source?.AllowHttp==true;
            if(existing is not null)
            {
                NameField.IsReadOnly=true;PathField.Text=existing.LocalPath;ChooseButton.IsEnabled=false;
                var credentials=model.ReadCredentials(existing.Id);UserField.Text=credentials.UserName;PasswordField.Password=credentials.Password;_deviceToken=credentials.SessionToken;
                var parts=source!.Prefix[1..].Split('/',2);ShareField.ItemsSource=new[]{new SynologySharedFolder(parts[0],"/"+parts[0])};ShareField.SelectedIndex=0;SubfolderField.Text=parts.Length>1?parts[1]:"";
                AutoConnectField.IsOn=existing.AutoConnect;AutoConnectField.IsEnabled=false;
                RemoteAuto.IsOn=existing.RemoteAutoDetectEnabled;IncrementalField.Value=existing.RemoteIncrementalSeconds;FullField.Value=existing.RemoteFullSeconds;
                CapacityAuto.IsOn=existing.CapacityAutoDetectEnabled;MaximumField.Text=existing.MaximumCapacityBytes is {} maximum?((decimal)maximum/1_000_000_000m).ToString(System.Globalization.CultureInfo.CurrentCulture):"";
                ThumbnailToggle.IsOn=model.ThumbnailDraft(existing);ThumbnailToggle.IsEnabled=model.CanConfigureThumbnails;
                ExistingSettings.Visibility=SaveIntervalsButton.Visibility=Visibility.Visible;
            }
            Title=existing is null?"新增 · 群晖 NAS":"设置 · "+existing.Name;
            PrimaryButtonText=existing is null?"创建并连接":"保存连接并重连";SecondaryButtonText="测试连接";CloseButtonText="关闭";
            DefaultButton=existing is null?ContentDialogButton.Primary:ContentDialogButton.Close;
            PrimaryButtonClick+=SaveConnection;SecondaryButtonClick+=TestConnection;Closing+=OnClosing;Closed+=OnClosed;
            model.Refreshed+=Refresh;model.StateChanged+=Refresh;model.PropertyChanged+=ModelPropertyChanged;_loading=false;Refresh();UpdatePin();UpdateHttpWarning();
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
        if(_disposed)return;if(_model.IsStopping){CancelForShutdown();return;}
        var mount=_existing is {} existing?_model.FindMount(existing.Id):null;if(_existing is not null&&mount is null){CancelForShutdown();return;}
        var busy=_submitting||_picking||_testing is not null||mount is not null&&!MountActionState.From(mount,_model.IsBusy(mount.Definition.Id)).CanEdit;
        ConnectionFields.IsEnabled=SettingsFields.IsEnabled=!busy;IsPrimaryButtonEnabled=!busy;IsSecondaryButtonEnabled=!_submitting&&!_picking&&(_testing is not null||!busy);
        EndpointField.IsReadOnly=SubfolderField.IsReadOnly=!_rootEditable;ShareField.IsEnabled=HttpField.IsEnabled=_rootEditable;
        if(mount is not null){DetectionDescription.Text=MountsViewModel.DetectionSummary(mount);CapacityDescription.Text=MountsViewModel.CapacitySummary(mount);}
        StatusText.Text=_model.Status;
    }
    private void ModelPropertyChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs args)
    {if(!Stopped&&args.PropertyName==nameof(MountsViewModel.Status))StatusText.Text=_model.Status;}
    private string Prefix=>ShareField.SelectedItem is SynologySharedFolder share?share.Path+(SubfolderField.Text.Length==0?"":"/"+SubfolderField.Text):"";
    private FileSourceConfiguration ReadSettings(bool requireShare)
    {
        if(requireShare&&ShareField.SelectedItem is not SynologySharedFolder)throw new ArgumentException("请先测试连接并选择共享文件夹。");
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
    private void UpdateHttpWarning()=>HttpWarning.IsOpen=Uri.TryCreate(EndpointField.Text.Trim(),UriKind.Absolute,out var uri)&&uri.Scheme=="http";
    private void UpdatePin(){PinText.Text=_pin is null?"证书验证：系统信任证书。":"本挂载固定的证书 SHA-256：\n"+_pin;ClearPinButton.Visibility=_pin is null?Visibility.Collapsed:Visibility.Visible;}
    private void ClearPin(object sender,RoutedEventArgs args){if(Stopped)return;Invalidate(false);_pin=null;UpdatePin();}
    private async void TrustCertificate(object sender,RoutedEventArgs args)
    {if(Stopped||_testing is not null||_submitting||_certificateCandidate is null)return;_pin=_certificateCandidate;Invalidate(false);UpdatePin();await ProbeAsync();}
    private void ShowError(string message){ErrorBar.Message=message;ErrorBar.IsOpen=true;}
    private async Task<bool> ProbeAsync()
    {
        if(Stopped)return false;
        using var cancel=new CancellationTokenSource();_testing=cancel;_verified=null;_certificateCandidate=null;CertificateBar.IsOpen=false;Refresh();if(Stopped){_testing=null;return false;}
        SecondaryButtonText="取消测试";ErrorBar.IsOpen=false;ProbeBar.IsOpen=true;ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.Message="正在执行只读探测…";
        try
        {
            var settings=ReadSettings(false);var result=await _model.TestSynologyConnectionAsync(settings,ReadCredentials(),OtpField.Password,cancel.Token,_probe?.Session);
            if(Stopped){await result.DisposeAsync();return false;}
            if(_probe is not null)await _probe.DisposeAsync();_probe=result;OtpField.Password="";ProbeBar.Message=result.Message;ProbeBar.Severity=result.Success?InfoBarSeverity.Success:InfoBarSeverity.Error;
            if(result.CertificateSha256 is {} fingerprint){_certificateCandidate=fingerprint;CertificateBar.Message="请通过 NAS 管理渠道核对以下 SHA-256；不会修改系统或其他挂载的信任设置：\n"+fingerprint;CertificateBar.IsOpen=true;}
            if(result.AuthenticationCode is 403 or 404 or 406){OtpField.Visibility=Visibility.Visible;OtpField.Focus(FocusState.Programmatic);}
            if(result.Credentials is null)return false;
            _deviceToken=result.Credentials.SessionToken;
            var selected=ShareField.SelectedItem as SynologySharedFolder;
            _loading=true;
            try{ShareField.ItemsSource=result.Shares;ShareField.SelectedItem=result.Shares.FirstOrDefault(x=>x.Path==selected?.Path);if(ShareField.SelectedItem is null&&_rootEditable&&result.Shares.Count>0)ShareField.SelectedIndex=0;}
            finally{_loading=false;}
            if(!result.Success)return false;
            CertificateBar.IsOpen=false;_certificateCandidate=null;
            _verified=settings.Prefix.Length!=0&&settings.Prefix==Prefix?Identity(settings):null;
            if(result.Shares.Count==0){ShowError("该账号没有可访问的共享文件夹，不能创建挂载。");return false;}
            return true;
        }
        catch(OperationCanceledException){if(!Stopped)ProbeBar.Message="连接测试已取消。";return false;}
        catch(Exception error){if(!Stopped){ProbeBar.Severity=InfoBarSeverity.Error;ProbeBar.Message=error.Message;}return false;}
        finally{_testing=null;if(!Stopped){OtpField.Password="";SecondaryButtonText="测试连接";Refresh();}}
    }
    private async void TestConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {args.Cancel=true;if(Stopped)return;if(_testing is {} testing){testing.Cancel();return;}if(!_submitting&&!_picking)await ProbeAsync();}
    private static int Seconds(double value,int minimum,int maximum)
    {if(!double.IsFinite(value)||value!=Math.Truncate(value)||value<minimum||value>maximum)throw new ArgumentException("请输入范围内的整数检测间隔。");return checked((int)value);}
    private async void SaveConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;if(Stopped||_submitting||_picking||_testing is not null)return;var deferral=args.GetDeferral();_submitting=true;Refresh();
        try
        {
            if(Stopped)return;ErrorBar.IsOpen=false;
            var incremental=Seconds(IncrementalField.Value,10,86400);var full=Seconds(FullField.Value,30,604800);
            if(_verified!=Identity(ReadSettings(false))&&!await ProbeAsync())return;
            if(Stopped)return;var settings=ReadSettings(true);
            if(_verified!=Identity(settings)){if(!await ProbeAsync()||Stopped||_verified!=Identity(ReadSettings(true)))return;settings=ReadSettings(true);}
            using var authenticated=SynologySessionTicket.Use(_probe?.Session);
            var result=await _model.SaveConnectionAsync(_existing?.Id,NameField.Text,PathField.Text,AutoConnectField.IsOn,settings,ReadCredentials());
            if(Stopped)return;CreatedMount=result.Mount;ResultMessage=result.Message;
            _model.UpdateDraft(result.Mount.Id,RemoteAuto.IsOn,incremental,full);await _model.SaveDetectionAsync(result.Mount.Id);
            if(Stopped)return;
            var saved=_model.FindMount(result.Mount.Id)?.Definition;
            if(saved is null||saved.RemoteAutoDetectEnabled!=RemoteAuto.IsOn||saved.RemoteIncrementalSeconds!=incremental||saved.RemoteFullSeconds!=full){ShowError("挂载已保存，但检测设置未完成："+_model.Status);return;}
            args.Cancel=false;
        }
        catch(OperationCanceledException)when(_model.IsStopping){args.Cancel=false;}
        catch(Exception error){if(!Stopped)ShowError(error.Message);}
        finally{_submitting=false;if(!Stopped)Refresh();deferral.Complete();}
    }
    private async void SaveIntervals(object sender,RoutedEventArgs args)
    {if(Stopped||_existing is null)return;try{_model.UpdateDraft(_existing.Id,RemoteAuto.IsOn,Seconds(IncrementalField.Value,10,86400),Seconds(FullField.Value,30,604800));await _model.SaveDetectionAsync(_existing.Id);}catch(Exception error){if(!Stopped)ShowError(error.Message);}}
    private async void SaveCapacity(object sender,RoutedEventArgs args)
    {if(Stopped||_existing is null)return;_model.UpdateCapacityDraft(_existing.Id,CapacityAuto.IsOn,MaximumField.Text,"GB");await _model.SaveCapacityAsync(_existing.Id);}
    private async void RecheckCapacity(object sender,RoutedEventArgs args){if(!Stopped&&_existing is {} mount)await _model.RecheckCapacityAsync(mount.Id);}
    private async void SaveThumbnails(object sender,RoutedEventArgs args){if(!Stopped&&_existing is {} mount)await _model.SetThumbnailAsync(mount.Id,ThumbnailToggle.IsOn);}
    private async void ChooseFolder(object sender,RoutedEventArgs args)
    {
        if(Stopped||_submitting||_picking||_testing is not null)return;_picking=true;Refresh();
        try{if(Stopped)return;var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);var folder=await picker.PickSingleFolderAsync();if(!Stopped&&folder is not null)PathField.Text=folder.Path;}
        catch(Exception error){if(!Stopped)ShowError(error.Message);}finally{_picking=false;if(!Stopped)Refresh();}
    }
    private void OnClosing(ContentDialog sender,ContentDialogClosingEventArgs args)
    {_testing?.Cancel();if(!_stopping&&(_submitting||_picking||_existing is {} mount&&_model.IsBusy(mount.Id)))args.Cancel=true;}
    private void OnClosed(ContentDialog sender,ContentDialogClosedEventArgs args)=>Dispose();
    public void CancelForShutdown(){if(_disposed)return;_stopping=true;_testing?.Cancel();Hide();Dispose();}
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_testing?.Cancel();PasswordField.Password=OtpField.Password="";_deviceToken="";_probe?.Session?.Discard();_probe=null;
        _model.Refreshed-=Refresh;_model.StateChanged-=Refresh;_model.PropertyChanged-=ModelPropertyChanged;PrimaryButtonClick-=SaveConnection;SecondaryButtonClick-=TestConnection;Closing-=OnClosing;Closed-=OnClosed;
    }
}
