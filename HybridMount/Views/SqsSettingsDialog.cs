using HybridMount.Models.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Views;

/// <summary>每挂载独立队列设置；测试只读属性，忙时防重复提交，凭据仅在控件及 DPAPI 内存中使用。</summary>
public sealed class SqsSettingsDialog : ContentDialog
{
    private readonly MountDefinition _mount;
    private readonly ToggleSwitch _enabled=new(){Header="启用 SQS 事件加速"};
    private readonly TextBox _url=new(){Header="Standard Queue URL"};
    private readonly TextBox _region=new(){Header="队列区域（必须与 S3 一致）"};
    private readonly ComboBox _mode=new(){Header="队列凭据来源",HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly TextBox _access=new(){Header="独立访问密钥 ID"};
    private readonly PasswordBox _secret=new(){Header="独立访问密钥"};
    private readonly PasswordBox _session=new(){Header="临时会话令牌（可选）"};
    private readonly Button _test=new(){Content="测试连接（只读）"};
    private readonly TextBlock _result=new(){TextWrapping=TextWrapping.Wrap};
    private CancellationTokenSource? _probe;
    private bool _saving;
    public SqsSettingsDialog(MountDefinition mount)
    {
        _mount=mount;Title="S3 远端检测与事件加速";PrimaryButtonText="保存";CloseButtonText="取消";DefaultButton=ContentDialogButton.Close;
        var options=mount.Sqs??new();_enabled.IsOn=options.Enabled;_url.Text=options.QueueUrl;_region.Text=options.Region.Length>0?options.Region:mount.Source!.Region;
        _mode.ItemsSource=new[]{"明确复用 S3 凭据","SDK 默认凭据链","独立访问密钥"};_mode.SelectedIndex=(int)options.CredentialMode;
        var keys=App.Current.Mounts.GetSqsCredentials(mount.Id);_access.Text=keys.AccessKey;_secret.Password=keys.SecretKey;_session.Password=keys.SessionToken;
        var stack=new StackPanel{Spacing=10,Width=520};
        stack.Children.Add(new TextBlock{Text="启用远端自动检测后，SQS 事件加速变化检测，定时完整对账继续保留。首版支持 AWS S3 原生通知 → 同区域 Standard Queue；R2 仅使用定时对账。每台机器请使用独立专用队列，避免竞争消费漏提示；本程序不创建队列、不修改桶通知，请按文档配置通知、策略及 DLQ。",TextWrapping=TextWrapping.Wrap});
        foreach(var control in new FrameworkElement[]{_enabled,_url,_region,_mode,_access,_secret,_session,_test,_result})stack.Children.Add(control);
        Content=new ScrollViewer{Content=stack,MaxHeight=650};_mode.SelectionChanged+=(_,_)=>UpdateFields();UpdateFields();
        _test.Click+=Test;PrimaryButtonClick+=Save;Closing+=(_,_)=>CancelForShutdown();
    }
    private SqsConfiguration Options()=>new(_enabled.IsOn,_url.Text.Trim(),_region.Text.Trim(),(SqsCredentialMode)_mode.SelectedIndex);
    private SourceCredentials Keys()=>new(AccessKey:_access.Text.Trim(),SecretKey:_secret.Password,SessionToken:_session.Password);
    private void UpdateFields(){var separate=_mode.SelectedIndex==(int)SqsCredentialMode.SeparateKeys;_access.IsEnabled=separate;_secret.IsEnabled=separate;_session.IsEnabled=separate;}
    private async void Test(object sender,RoutedEventArgs args)
    {
        if(_probe is not null){_probe.Cancel();return;}if(_saving)return;
        var probe=new CancellationTokenSource();_probe=probe;IsPrimaryButtonEnabled=false;_test.Content="取消测试";
        try{var result=await App.Current.Mounts.TestSqsConnectionAsync(_mount.Id,Options(),Keys(),probe.Token);_result.Text=result.Message;}
        catch(OperationCanceledException){_result.Text="测试已取消。";}
        catch(Exception error){_result.Text=error.Message;}
        finally{_probe=null;probe.Dispose();_test.Content="测试连接（只读）";IsPrimaryButtonEnabled=true;}
    }
    private async void Save(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        if(_saving||_probe is not null){args.Cancel=true;return;}_saving=true;IsPrimaryButtonEnabled=false;_test.IsEnabled=false;var deferral=args.GetDeferral();
        try{await App.Current.Mounts.SetSqsConfigurationAsync(_mount.Id,Options(),Keys());}
        catch(Exception error){args.Cancel=true;_result.Text=error.Message;}
        finally{_saving=false;IsPrimaryButtonEnabled=true;_test.IsEnabled=true;deferral.Complete();}
    }
    /// <summary>页面或应用停止时只发出取消，不先释放仍在使用的令牌。</summary>
    public void CancelForShutdown()=>_probe?.Cancel();
}
