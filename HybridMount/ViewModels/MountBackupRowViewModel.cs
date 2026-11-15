using HybridMount.Models.Configuration;
using HybridMount.Models.Localization;
using HybridMount.Models.Mounting;
using HybridMount.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
namespace HybridMount.ViewModels;

/// <summary>列表只绑定非秘密说明，凭据不参与字符串展示；选择和目录仅在当前对话框中编辑。</summary>
public sealed class MountBackupRowViewModel : ObservableObject
{
    public MountBackupItem Configuration { get; }
    public Guid? ExistingId { get; }
    private bool _selected = true, _canSelect = true;
    private string _localPath, _status = "";
    public string Name => Configuration.Name;
    public string SourceDisplay
    {
        get
        {
            var source = Configuration.Source;
            var address = Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? source.Endpoint : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("InvalidAddress", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
            return source.Kind switch
            {
                FileSourceKind.S3 => string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("S3BucketPrefix", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, address, source.Bucket, source.Prefix),
                FileSourceKind.Synology => string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SynologyNASRemoteRoot", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, address, source.Prefix),
                _ => $"{source.Kind}：{address}"
            };
        }
    }
    public string OptionsDisplay => string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ConnectAtApplicationStartupThumbnails", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, (Configuration.AutoConnect ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Yes", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("BooleanNo", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!), (Configuration.ThumbnailsEnabled ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SwitchOn", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Close", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!), (Configuration.Source.Kind == FileSourceKind.S3 && Configuration.Source.UseDefaultCredentials ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("UseTheDestinationMachineSDefaultSDKCredentialChain", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : ""));
    public bool IsImporting { get; }
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public bool CanSelect { get => _canSelect; set => SetProperty(ref _canSelect, value); }
    public string LocalPath { get => _localPath; set => SetProperty(ref _localPath, value); }
    private global::HybridMount.Models.Localization.ResourceMessage? _statusMessage;
    public string StatusDisplay => _statusMessage?.Render() ?? _status;
    public void SetStatus(global::HybridMount.Models.Localization.ResourceMessage message) { _statusMessage = message; SetProperty(ref _status, message.Render(), nameof(Status)); OnPropertyChanged(nameof(StatusDisplay)); }
    public string Status { get => _status; set { _statusMessage = null; SetProperty(ref _status, value); OnPropertyChanged(nameof(StatusDisplay)); } }
    public MountBackupRowViewModel(MountBackupItem configuration, bool importing, Guid? id = null)
    { Configuration = configuration; ExistingId = id; _localPath = configuration.LocalPath; IsImporting = importing; }
}
