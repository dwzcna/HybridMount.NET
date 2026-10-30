using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace HybridMount.Views;

/// <summary>创建网络挂载的表单；验证失败保留输入，持久创建后连接失败则返回列表重试。</summary>
public sealed partial class NetworkMountDialog : ContentDialog
{
    private bool _submitting,_picking;
    private CancellationTokenSource? _testing;
    /// <summary>已持久创建的挂载；取消或校验失败时为空。</summary>
    public MountDefinition? CreatedMount { get; private set; }
    /// <summary>返回挂载页显示的创建或连接结果。</summary>
    public string ResultMessage { get; private set; }="";
    /// <summary>初始化新表单；目录在本次对话框内选择。</summary>
    private readonly MountDefinition? _existing;
    private readonly FileSourceKind _kind;
    public NetworkMountDialog(FileSourceKind kind=FileSourceKind.WebDav,MountDefinition? existing=null)
    {
        InitializeComponent();_existing=existing;_kind=kind;AutoUploadField.Visibility=existing is null?Visibility.Visible:Visibility.Collapsed;
        if(kind is not(FileSourceKind.WebDav or FileSourceKind.S3))throw new NotSupportedException("此协议暂未提供创建入口。");
        var s3=kind==FileSourceKind.S3;Title=s3?"S3 对象存储挂载":"WebDAV 挂载";NameField.Text=s3?"S3 挂载":"WebDAV 挂载";
        DavFields.Visibility=s3?Visibility.Collapsed:Visibility.Visible;S3Fields.Visibility=s3?Visibility.Visible:Visibility.Collapsed;
        EndpointField.PlaceholderText=s3?"https://s3.example.com":"https://dav.example.com/dav/";
        if(existing is not null)
        {
            Title="更新连接凭据";PrimaryButtonText="保存并重连";
            NameField.Text=existing.Name;NameField.IsReadOnly=true;PathField.Text=existing.LocalPath;ChooseButton.IsEnabled=false;
            var editable=App.Current.Mounts.CanEditRemoteRoot(existing.Id);
            Title=editable?"编辑连接配置":"更新连接凭据";
            EndpointField.Text=existing.Source!.Endpoint;EndpointField.IsReadOnly=!editable;HttpField.IsChecked=existing.Source.AllowHttp;HttpField.IsEnabled=editable;
            AutoConnectField.IsOn=existing.AutoConnect;AutoConnectField.IsEnabled=false;
            var saved=App.Current.Mounts.GetCredentials(existing.Id);UserField.Text=saved.UserName;
            PasswordField.Password=saved.Password;
            BucketField.Text=existing.Source.Bucket;BucketField.IsReadOnly=!editable;PrefixField.Text=existing.Source.Prefix;PrefixField.IsReadOnly=!editable;
            RegionField.Text=existing.Source.Region;PathStyleField.IsChecked=existing.Source.PathStyle;
            DefaultCredentialsField.IsChecked=existing.Source.UseDefaultCredentials;AccessKeyField.Text=saved.AccessKey;
            SecretKeyField.Password=saved.SecretKey;SessionTokenField.Password=saved.SessionToken;
        }
    }
    private void CredentialsModeChanged(object sender,RoutedEventArgs args) { if(AccessKeyField is null)return;var enabled=DefaultCredentialsField.IsChecked!=true;AccessKeyField.IsEnabled=enabled;SecretKeyField.IsEnabled=enabled;SessionTokenField.IsEnabled=enabled; }
    private void OnClosing(ContentDialog sender,ContentDialogClosingEventArgs args) { if(_testing is not null)_testing.Cancel();if(_submitting || _picking)args.Cancel=true; }
    private async void ChooseFolder(object sender,RoutedEventArgs args)
    {
        if(_submitting || _picking || _testing is not null)return;
        _picking=true;IsPrimaryButtonEnabled=false;IsSecondaryButtonEnabled=false;
        try
        {
            var picker=new FolderPicker();picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
            var folder=await picker.PickSingleFolderAsync();if(folder is not null)PathField.Text=folder.Path;
        }
        catch(Exception error) { ErrorBar.Message=error.Message;ErrorBar.IsOpen=true; }
        finally { _picking=false;IsPrimaryButtonEnabled=true;IsSecondaryButtonEnabled=true; }
    }
    private FileSourceConfiguration Settings()=>new(_kind,EndpointField.Text.Trim(),BucketField.Text.Trim(),PrefixField.Text.Trim().Trim('/'),RegionField.Text.Trim(),PathStyleField.IsChecked==true,HttpField.IsChecked==true,DefaultCredentialsField.IsChecked==true);
    private SourceCredentials Credentials()=>_kind==FileSourceKind.WebDav?new(UserField.Text.Trim(),PasswordField.Password):new(AccessKey:AccessKeyField.Text.Trim(),SecretKey:SecretKeyField.Password,SessionToken:SessionTokenField.Password);
    /// <summary>UI 线程处理固定次按钮；保持对话框打开，测试中再次点击则取消请求。</summary>
    /// <remarks>网络等待不持有按钮 deferral，以便继续使用取消测试和关闭按钮。</remarks>
    private async void TestConnection(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;
        if(_testing is {} testing)
        {
            SecondaryButtonText="取消中…";IsSecondaryButtonEnabled=false;
            testing.Cancel();return;
        }
        if(_submitting||_picking)return;
        using var cancel=new CancellationTokenSource();_testing=cancel;
        var settings=Settings();var credentials=Credentials();
        Form.IsEnabled=false;IsPrimaryButtonEnabled=false;IsSecondaryButtonEnabled=true;SecondaryButtonText="取消测试";ErrorBar.IsOpen=false;
        ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.Message="正在执行只读探测…";ProbeBar.IsOpen=true;
        try
        {
            var result=await new FileSourceFactory().TestConnectionAsync(settings,credentials,cancel.Token);
            ProbeBar.Severity=result.Success?InfoBarSeverity.Success:InfoBarSeverity.Error;ProbeBar.Message=result.Message;
        }
        catch(OperationCanceledException) { ProbeBar.Severity=InfoBarSeverity.Informational;ProbeBar.Message="连接测试已取消。"; }
        catch(Exception) { ProbeBar.Severity=InfoBarSeverity.Error;ProbeBar.Message="连接测试失败，请检查配置、网络和凭据来源。"; }
        finally
        { _testing=null;Form.IsEnabled=true;IsPrimaryButtonEnabled=true;IsSecondaryButtonEnabled=true;SecondaryButtonText="测试连接"; }
    }
    private async void CreateMount(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;
        if(_submitting || _picking || _testing is not null)return;
        var deferral=args.GetDeferral();_submitting=true;Form.IsEnabled=false;IsPrimaryButtonEnabled=false;IsSecondaryButtonEnabled=false;ErrorBar.IsOpen=false;
        var before=App.Current.Mounts.Mounts.Select(mount=>mount.Definition.Id).ToHashSet();
        try
        {
            var settings=Settings();var credentials=Credentials();
            if(_existing is null)CreatedMount=await App.Current.Mounts.AddAsync(NameField.Text,PathField.Text,AutoConnectField.IsOn,source:settings,credentials:credentials,autoUpload:AutoUploadField.IsOn);
            else { await App.Current.Mounts.UpdateCredentialsAsync(_existing.Id,credentials,settings:settings);CreatedMount=_existing; }
            ResultMessage=$"已连接 {CreatedMount.Name}。";args.Cancel=false;
        }
        catch(Exception error)
        {
            var created=_existing is null?App.Current.Mounts.Mounts.FirstOrDefault(mount=>!before.Contains(mount.Definition.Id)&&mount.Definition.LocalPath.Equals(PathField.Text,StringComparison.OrdinalIgnoreCase)):App.Current.Mounts.Mounts.FirstOrDefault(mount=>mount.Definition.Id==_existing.Id);
            if(created is not null && error is not (ArgumentException or NotSupportedException))
            {
                CreatedMount=created.Definition;ResultMessage=$"连接未完成：{error.Message}；已保存的配置保留，可编辑连接配置或重试。";args.Cancel=false;
            }
            else { ErrorBar.Message=error.Message;ErrorBar.IsOpen=true; }
            App.Current.Logger.Queue("新增挂载","Error",error.Message);
        }
        finally { _submitting=false;Form.IsEnabled=true;IsPrimaryButtonEnabled=true;IsSecondaryButtonEnabled=true;deferral.Complete(); }
    }
}
