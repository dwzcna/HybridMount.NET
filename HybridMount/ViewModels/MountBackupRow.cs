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
            var address = Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? source.Endpoint : "地址无效";
            return source.Kind == FileSourceKind.S3 ? $"S3：{address}；桶 {source.Bucket}；前缀 {source.Prefix}" : $"{source.Kind}：{address}";
        }
    }
    public string OptionsDisplay => $"随程序启动连接：{(Configuration.AutoConnect ? "是" : "否")}；缩略图：{(Configuration.ThumbnailsEnabled ? "开启" : "关闭")}" +
        (Configuration.Source.Kind == FileSourceKind.S3 && Configuration.Source.UseDefaultCredentials ? "；使用目标机器的 SDK 默认凭据链" : "");
    public bool IsImporting { get; }
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public bool CanSelect { get => _canSelect; set => SetProperty(ref _canSelect, value); }
    public string LocalPath { get => _localPath; set => SetProperty(ref _localPath, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public MountBackupRow(MountBackupItem configuration, bool importing, Guid? id = null)
    { Configuration = configuration; ExistingId = id; _localPath = configuration.LocalPath; IsImporting = importing; }
}
