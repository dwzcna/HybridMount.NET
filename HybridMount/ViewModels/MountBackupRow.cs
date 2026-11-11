using CommunityToolkit.Mvvm.ComponentModel;
namespace HybridMount.ViewModels;

/// <summary>列表只绑定非秘密说明，凭据不参与字符串展示；选择和目录仅在当前对话框中编辑。</summary>
public sealed class MountBackupRow : ObservableObject
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
            var address = Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? source.Endpoint : global::HybridMount.Services.Localization.UiText.Text("Text_5CC09FEC5D5D");
            return source.Kind switch
            {
                FileSourceKind.S3=>global::HybridMount.Services.Localization.UiText.Text("Text_AC3FFDC297B7",address,source.Bucket,source.Prefix),
                FileSourceKind.Synology=>global::HybridMount.Services.Localization.UiText.Text("Text_6A4F3B622778",address,source.Prefix),
                _=>$"{source.Kind}：{address}"
            };
        }
    }
    public string OptionsDisplay => global::HybridMount.Services.Localization.UiText.Text("Text_54D9C129C871",(Configuration.AutoConnect ? global::HybridMount.Services.Localization.UiText.Text("Text_B5141D3D19E9") : global::HybridMount.Services.Localization.UiText.Text("Text_0C70665B6EB6")),(Configuration.ThumbnailsEnabled ? global::HybridMount.Services.Localization.UiText.Text("Text_8DA97DDDA990") : global::HybridMount.Services.Localization.UiText.Text("Text_3FD47EDCE45B")),(Configuration.Source.Kind == FileSourceKind.S3 && Configuration.Source.UseDefaultCredentials ? global::HybridMount.Services.Localization.UiText.Text("Text_5F03A03C17D2") : ""));
    public bool IsImporting { get; }
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public bool CanSelect { get => _canSelect; set => SetProperty(ref _canSelect, value); }
    public string LocalPath { get => _localPath; set => SetProperty(ref _localPath, value); }
    public string StatusDisplay=>global::HybridMount.Services.Localization.UiText.Legacy(_status);
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public MountBackupRow(MountBackupItem configuration, bool importing, Guid? id = null)
    { Configuration = configuration; ExistingId = id; _localPath = configuration.LocalPath; IsImporting = importing; }
}
