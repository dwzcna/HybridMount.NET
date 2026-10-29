using HybridMount.Models.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace HybridMount.Views;

/// <summary>创建网络挂载的表单；验证失败保留输入，持久创建后连接失败则返回列表重试。</summary>
public sealed partial class NetworkMountDialog : ContentDialog
{
    private bool _submitting,_picking;
    /// <summary>已持久创建的挂载；取消或校验失败时为空。</summary>
    public MountDefinition? CreatedMount { get; private set; }
    /// <summary>返回挂载页显示的创建或连接结果。</summary>
    public string ResultMessage { get; private set; }="";
    /// <summary>初始化新表单；目录在本次对话框内选择。</summary>
    private readonly MountDefinition? _existing;
    public NetworkMountDialog(FileSourceKind kind=FileSourceKind.WebDav,MountDefinition? existing=null)
    {
        InitializeComponent();_existing=existing;
        if(existing is not null)
        {
            Title="更新连接凭据";PrimaryButtonText="保存并重连";
            NameField.Text=existing.Name;NameField.IsReadOnly=true;PathField.Text=existing.LocalPath;ChooseButton.IsEnabled=false;
            EndpointField.Text=existing.Source!.Endpoint;EndpointField.IsReadOnly=true;HttpField.IsChecked=existing.Source.AllowHttp;HttpField.IsEnabled=false;
            AutoConnectField.IsOn=existing.AutoConnect;AutoConnectField.IsEnabled=false;
            var saved=App.Current.Mounts.GetCredentials(existing.Id);UserField.Text=saved.UserName;
            PasswordField.Password=saved.Password;
        }
    }
    private void OnClosing(ContentDialog sender,ContentDialogClosingEventArgs args) { if(_submitting || _picking)args.Cancel=true; }
    private async void ChooseFolder(object sender,RoutedEventArgs args)
    {
        if(_submitting || _picking)return;
        _picking=true;IsPrimaryButtonEnabled=false;
        try
        {
            var picker=new FolderPicker();picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
            var folder=await picker.PickSingleFolderAsync();if(folder is not null)PathField.Text=folder.Path;
        }
        catch(Exception error) { ErrorBar.Message=error.Message;ErrorBar.IsOpen=true; }
        finally { _picking=false;IsPrimaryButtonEnabled=true; }
    }
    private async void CreateMount(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        args.Cancel=true;
        if(_submitting || _picking)return;
        var deferral=args.GetDeferral();_submitting=true;Form.IsEnabled=false;IsPrimaryButtonEnabled=false;ErrorBar.IsOpen=false;
        var before=App.Current.Mounts.Mounts.Select(mount=>mount.Definition.Id).ToHashSet();
        try
        {
            var settings=new FileSourceConfiguration(FileSourceKind.WebDav,EndpointField.Text.Trim(),AllowHttp:HttpField.IsChecked==true);
            var credentials=new SourceCredentials(UserField.Text.Trim(),PasswordField.Password);
            if(_existing is null)CreatedMount=await App.Current.Mounts.AddAsync(NameField.Text,PathField.Text,AutoConnectField.IsOn,source:settings,credentials:credentials);
            else { await App.Current.Mounts.UpdateCredentialsAsync(_existing.Id,credentials);CreatedMount=_existing; }
            ResultMessage=$"已连接 {CreatedMount.Name}。";args.Cancel=false;
        }
        catch(Exception error)
        {
            var created=_existing is null?App.Current.Mounts.Mounts.FirstOrDefault(mount=>!before.Contains(mount.Definition.Id)&&mount.Definition.LocalPath.Equals(PathField.Text,StringComparison.OrdinalIgnoreCase)):App.Current.Mounts.Mounts.FirstOrDefault(mount=>mount.Definition.Id==_existing.Id);
            if(created is not null)
            {
                CreatedMount=created.Definition;ResultMessage=$"挂载已创建，但连接失败：{error.Message}；请选择该挂载后点击连接重试。";args.Cancel=false;
            }
            else { ErrorBar.Message=error.Message;ErrorBar.IsOpen=true; }
            App.Current.Logger.Queue("新增挂载","Error",error.Message);
        }
        finally { _submitting=false;Form.IsEnabled=true;IsPrimaryButtonEnabled=true;deferral.Complete(); }
    }
}
