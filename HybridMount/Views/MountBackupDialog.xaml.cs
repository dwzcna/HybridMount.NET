using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace HybridMount.Views;

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
    public Visibility FolderButtonVisibility { get; }
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public bool CanSelect { get => _canSelect; set => SetProperty(ref _canSelect, value); }
    public string LocalPath { get => _localPath; set => SetProperty(ref _localPath, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public MountBackupRow(MountBackupItem configuration, bool importing, Guid? id = null)
    { Configuration = configuration; ExistingId = id; _localPath = configuration.LocalPath; FolderButtonVisibility = importing ? Visibility.Visible : Visibility.Collapsed; }
}

/// <summary>加密导出与分阶段导入的同一对话框；解密、校验和保存由应用服务拥有，取消不回滚已保存条目。</summary>
public sealed partial class MountBackupDialog : ContentDialog
{
    private readonly bool _importing;
    private readonly string? _inputPath;
    private readonly ObservableCollection<MountBackupRow> _rows = [];
    private readonly CancellationTokenSource _dialogStop = new();
    private CancellationTokenSource? _active;
    private bool _working, _decoded, _finished, _applyingPreview, _cancelRequested, _closed;
    private int _previewVersion;
    public string ResultMessage { get; private set; } = "";
    public IReadOnlyList<Guid> ImportedIds { get; private set; } = [];

    /// <summary>inputPath 为空时导出；否则先解密再预览，导入不立即连接。</summary>
    public MountBackupDialog(string? inputPath = null)
    {
        InitializeComponent(); _inputPath = inputPath; _importing = inputPath is not null; RowsList.ItemsSource = _rows;
        Title = _importing ? "导入配置" : "导出配置"; PrimaryButtonText = _importing ? "解密并预览" : "导出";
        HelpText.Text = _importing ? "输入备份密码后预览。导入只新增并保存配置，不覆盖现有挂载，也不立即连接。" : "选择要备份的挂载。配置及账号、密码和密钥将使用备份密码加密，请记住密码。SDK 默认凭据链需在目标机器另行配置。";
        ConfirmPasswordField.Visibility = _importing ? Visibility.Collapsed : Visibility.Visible;
        RowsList.Visibility = _importing ? Visibility.Collapsed : Visibility.Visible;
        if (!_importing)
        {
            foreach (var mount in App.Current.Mounts.Mounts.Where(item => item.Definition.Source?.Kind is FileSourceKind.WebDav or FileSourceKind.S3))
            {
                var definition = mount.Definition;
                var row = new MountBackupRow(new(definition.Name, definition.LocalPath, definition.Source!, new(), definition.AutoConnect, definition.ThumbnailsEnabled), false, definition.Id);
                if (definition.RemovalPending) { row.Selected = false; row.CanSelect = false; row.Status = "移除中的挂载暂不可导出。"; }
                _rows.Add(row);
            }
            IsPrimaryButtonEnabled = _rows.Any(row => row.Selected);
            if (_rows.Count == 0) ShowStatus("当前没有可导出的 WebDAV 或 S3 挂载。", InfoBarSeverity.Informational);
        }
    }
    private MountImportItem[] Requests() => _rows.Select(row => new MountImportItem(row.Configuration, row.LocalPath, row.Selected)).ToArray();
    private void ShowStatus(string message, InfoBarSeverity severity)
    { if (_closed || App.Current.IsExiting) return; StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
    private void SetWorking(bool working)
    {
        _working = working; if (_closed || App.Current.IsExiting) return; Form.IsEnabled = !working && !_finished;
        IsPrimaryButtonEnabled = !working && !_finished && (!_decoded && _importing || _rows.Any(row => row.Selected && row.CanSelect));
    }
    private async void SelectionChanged(object sender, RoutedEventArgs args)
    {
        if (_working || _applyingPreview || _finished) return;
        if (_importing) await ValidateRowsAsync(); else IsPrimaryButtonEnabled = _rows.Any(row => row.Selected && row.CanSelect);
    }
    /// <summary>版本号丢弃旧预览结果；修改选择不会写配置，重复刷新不会将无效行重新勾选。</summary>
    private async Task ValidateRowsAsync()
    {
        if (!_decoded || _working || _finished || _dialogStop.IsCancellationRequested) return;
        var version = ++_previewVersion; IsPrimaryButtonEnabled = false;
        try
        {
            var results = await App.Current.Mounts.PreviewImportConfigurationsAsync(Requests(), _dialogStop.Token);
            if (version != _previewVersion || _working || App.Current.IsExiting || _dialogStop.IsCancellationRequested) return;
            _applyingPreview = true;
            try
            {
                foreach (var result in results)
                {
                    var row = _rows[result.Index]; row.Status = result.Message; row.CanSelect = result.State == "Ready";
                    if (!row.CanSelect) row.Selected = false;
                }
            }
            finally { _applyingPreview = false; }
            IsPrimaryButtonEnabled = _rows.Any(row => row.Selected && row.CanSelect);
            ShowStatus("核对本地目录后导入；已有或重叠目录需要跳过或重新选择。", InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!App.Current.IsExiting && !_dialogStop.IsCancellationRequested) ShowStatus(error.Message, InfoBarSeverity.Error); }
    }
    private async void ChooseFolder(object sender, RoutedEventArgs args)
    {
        if (_working || _finished || sender is not Button { Tag: MountBackupRow row }) return;
        ++_previewVersion; SetWorking(true);
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindow!.Handle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && !_closed && !_dialogStop.IsCancellationRequested && !App.Current.IsExiting) { row.LocalPath = folder.Path; row.Selected = true; }
        }
        catch (Exception error) { if (!App.Current.IsExiting) ShowStatus(error.Message, InfoBarSeverity.Error); }
        finally { SetWorking(false); if (!_closed && !App.Current.IsExiting) { if (_cancelRequested) Hide(); else await ValidateRowsAsync(); } }
    }
    private async void Submit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; if (_working || _finished) return;
        var deferral = args.GetDeferral(); ++_previewVersion;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_dialogStop.Token); _active = cancel; SetWorking(true);
        var exported = false;
        try
        {
            if (!_importing)
            {
                if (PasswordField.Password.Length < 8) throw new ArgumentException("备份密码至少需要 8 个字符。");
                if (!string.Equals(PasswordField.Password, ConfirmPasswordField.Password, StringComparison.Ordinal)) throw new ArgumentException("两次输入的备份密码不一致。");
                var ids = _rows.Where(row => row.Selected && row.CanSelect).Select(row => row.ExistingId!.Value).ToArray();
                if (ids.Length == 0) throw new ArgumentException("请选择要导出的挂载。");
                var picker = new FileSavePicker { SuggestedFileName = $"HybridMount-config-{DateTime.Now:yyyyMMdd-HHmmss}" };
                picker.FileTypeChoices.Add("HybridMount 加密配置", new List<string> { ".hmconfig" }); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindow!.Handle);
                var file = await picker.PickSaveFileAsync();
                if (_closed || App.Current.IsExiting) return;
                if (file is null) { ShowStatus("已取消文件选择。", InfoBarSeverity.Informational); return; }
                await App.Current.Mounts.ExportConfigurationsAsync(ids, PasswordField.Password, file.Path, cancel.Token);
                ResultMessage = $"已加密导出 {ids.Length} 个挂载配置。"; exported = true;
            }
            else if (!_decoded)
            {
                var document = await App.Current.Mounts.ReadConfigurationBackupAsync(_inputPath!, PasswordField.Password, cancel.Token);
                if (App.Current.IsExiting) return;
                _applyingPreview = true;
                try { foreach (var item in document.Mounts) _rows.Add(new MountBackupRow(item, true)); }
                finally { _applyingPreview = false; }
                _decoded = true; PasswordField.Password = ""; PasswordField.Visibility = Visibility.Collapsed; RowsList.Visibility = Visibility.Visible;
                PrimaryButtonText = "导入选中项"; HelpText.Text = "选择要新增的挂载并确认本地 NTFS 空目录。仅保存配置，之后从挂载列表手动连接；随启动连接与缩略图设置按备份恢复。";
            }
            else
            {
                var progress = new Progress<MountImportResult>(result => { if (!App.Current.IsExiting && !_dialogStop.IsCancellationRequested) _rows[result.Index].Status = result.Message; });
                var result = await App.Current.Mounts.ImportConfigurationsAsync(Requests(), cancel.Token, progress);
                ImportedIds = result.Results.Where(item => item.State == "Saved").Select(item => item.MountId!.Value).ToArray();
                if (_closed || App.Current.IsExiting) return;
                _applyingPreview = true;
                try { foreach (var item in result.Results) { _rows[item.Index].Status = item.Message; _rows[item.Index].CanSelect = false; } }
                finally { _applyingPreview = false; }
                var failed = result.Results.Count(item => item.State == "Failed"); var skipped = result.Results.Count(item => item.State == "Skipped");
                var canceled = result.Results.Count(item => item.State == "Canceled");
                ResultMessage = $"导入结果：已保存 {ImportedIds.Count}，跳过 {skipped}，失败 {failed}，取消 {canceled}。已保存的挂载尚未连接。";
                _finished = true; CloseButtonText = "关闭";
                ShowStatus(ResultMessage, failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException)
        { if (!App.Current.IsExiting) { ResultMessage = "配置操作已取消。已保存的条目保留，可在挂载列表查看。"; ShowStatus(ResultMessage, InfoBarSeverity.Informational); } }
        catch (Exception error) { if (!App.Current.IsExiting) ShowStatus(error.Message, InfoBarSeverity.Error); }
        finally
        {
            _active = null; SetWorking(false); deferral.Complete();
            if (!App.Current.IsExiting)
            {
                if (exported || _cancelRequested) Hide();
                else if (_decoded && !_finished) await ValidateRowsAsync();
            }
        }
    }
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_working) return; args.Cancel = true; _cancelRequested = true; CloseButtonText = "取消中…"; _active?.Cancel();
    }
    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    { _closed = true; _finished = true; _dialogStop.Cancel(); _active?.Cancel(); ++_previewVersion; PasswordField.Password = ""; ConfirmPasswordField.Password = ""; _rows.Clear(); _dialogStop.Dispose(); }
    /// <summary>窗口释放只请求停止；后台工作仍由应用拥有，不能越过退出状态提交。</summary>
    public void CancelForShutdown() { if (_closed) return; if (!_dialogStop.IsCancellationRequested) _dialogStop.Cancel(); _active?.Cancel(); }
}
