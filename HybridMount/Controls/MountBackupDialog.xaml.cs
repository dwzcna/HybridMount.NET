using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Mounting;
using HybridMount.Models.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Storage.Pickers;

namespace HybridMount.Controls;

/// <summary>可选凭据加密导出与自动识别导入；文件和密码处理在后台，UI 只展示状态，取消不回滚已保存条目。</summary>
public sealed partial class MountBackupDialog : ContentDialog
{
    private IDisposable? _localization;
    private readonly bool _importing;
    private readonly string? _inputPath;
    private readonly ObservableCollection<MountBackupRow> _rows = [];
    private readonly CancellationTokenSource _dialogStop = new();
    private CancellationTokenSource? _active;
    private bool _working, _decoded, _finished, _applyingPreview, _cancelRequested, _closed;
    private int _previewVersion;
    private MountBackupReadSession? _readSession;
    private bool _initialized, _loadStarted;
    public string ResultMessage { get; private set; } = "";
    public IReadOnlyList<Guid> ImportedIds { get; private set; } = [];

    /// <summary>inputPath 为空时导出；导入先识别保护模式，明文直接预览，导入不立即连接。</summary>
    public MountBackupDialog(string? inputPath = null)
    {
        _inputPath = inputPath; _importing = inputPath is not null;
        InitializeComponent();_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(this,()=>Bindings.Update()); RowsList.ItemsSource = _rows;
        ProtectionToggle.Visibility = UnprotectedNotice.Visibility = _importing ? Visibility.Collapsed : Visibility.Visible;
        global::HybridMount.Helpers.UI.Ui.Set(this,"Title",_importing ? global::HybridMount.Services.Localization.UiText.Text("Text_B48B6518293A") : global::HybridMount.Services.Localization.UiText.Text("Text_429EA3AF443D")); global::HybridMount.Helpers.UI.Ui.Set(this,"PrimaryButtonText",_importing ? global::HybridMount.Services.Localization.UiText.Text("Text_439537652398") : global::HybridMount.Services.Localization.UiText.Text("Text_76420433F22D"));
        ConfirmPasswordField.Visibility = _importing ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = false;
        RowsList.Visibility = _importing ? Visibility.Collapsed : Visibility.Visible;
        if (!_importing)
        {
            foreach (var mount in App.Current.Mounts.Mounts.Where(item => item.Definition.Source?.Kind is FileSourceKind.WebDav or FileSourceKind.S3 or FileSourceKind.Synology))
            {
                var definition = mount.Definition;
                var row = new MountBackupRow(new(definition.Name, definition.LocalPath, definition.Source!, new(), definition.AutoConnect, definition.ThumbnailsEnabled), false, definition.Id);
                if (definition.RemovalPending) { row.Selected = false; row.CanSelect = false; row.Status = global::HybridMount.Services.Localization.UiText.Text("Text_A39C7E51EDF4"); }
                _rows.Add(row);
            }
            IsPrimaryButtonEnabled = _rows.Any(row => row.Selected);
            if (_rows.Count == 0) ShowStatus(global::HybridMount.Services.Localization.UiText.Text("Text_5917CFAF5C13"), InfoBarSeverity.Informational);
        }
    }
    /// <summary>页面显示后后台读取一次；普通 JSON 不经过密码阶段，迟到结果必须释放而不能更新关闭的窗口。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _initialized = true; UpdatePrimaryButton();
        if (!_importing || _loadStarted || _closed) return;
        _loadStarted = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_dialogStop.Token); _active = cancel; SetWorking(true);
        try
        {
            var session = await App.Current.Mounts.OpenConfigurationBackupAsync(_inputPath!, cancel.Token);
            if (_closed || App.Current.IsExiting || cancel.IsCancellationRequested) { session.Dispose(); return; }
            _readSession = session;
            if (session.RequiresPassword) PasswordPanel.Visibility = Visibility.Visible;
            else AcceptDecoded(await App.Current.Mounts.DecodeConfigurationBackupAsync(session, "", cancel.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed && !App.Current.IsExiting) ShowStatus(error.Message, InfoBarSeverity.Error); }
        finally
        {
            _active = null; SetWorking(false);
            if (!_closed && !App.Current.IsExiting)
            { if (_cancelRequested) Hide(); else if (_decoded) await ValidateRowsAsync(); }
        }
    }
    /// <summary>只改变导出选项；关闭保护立即清除密码，不能将隐藏输入意外用于明文导出。</summary>
    private void ProtectionChanged(object sender, RoutedEventArgs args)
    {
        if (!_initialized || _importing || _closed) return;
        var enabled = ProtectionToggle.IsChecked == true;
        PasswordPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UnprotectedNotice.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (!enabled) { PasswordField.Password = ""; ConfirmPasswordField.Password = ""; }
        UpdatePrimaryButton();
    }
    private void PasswordChanged(object sender, RoutedEventArgs args)
    { if (_initialized && !_closed) UpdatePrimaryButton(); }
    private void UpdatePrimaryButton()
    {
        if (_closed || App.Current.IsExiting) return;
        var valid = _importing && !_decoded ? _readSession?.RequiresPassword == true && PasswordField.Password.Length >= 8 : _rows.Any(row => row.Selected && row.CanSelect);
        if (!_importing && ProtectionToggle.IsChecked == true)
            valid &= PasswordField.Password.Length >= 8 && string.Equals(PasswordField.Password, ConfirmPasswordField.Password, StringComparison.Ordinal);
        IsPrimaryButtonEnabled = !_working && !_finished && valid;
    }
    /// <summary>仅在 UI 线程接纳完整解码结果；关闭后丢弃，成功后立即归还文件快照并清空密码框。</summary>
    private void AcceptDecoded(MountConfigurationBackup document)
    {
        if (_closed || App.Current.IsExiting || _dialogStop.IsCancellationRequested) return;
        _applyingPreview = true;
        try { foreach (var item in document.Mounts) _rows.Add(new MountBackupRow(item, true)); }
        finally { _applyingPreview = false; }
        _decoded = true; _readSession?.Dispose(); _readSession = null;
        PasswordField.Password = ""; ConfirmPasswordField.Password = ""; PasswordPanel.Visibility = Visibility.Collapsed; RowsList.Visibility = Visibility.Visible;
        global::HybridMount.Helpers.UI.Ui.Set(this, "PrimaryButtonText", global::HybridMount.Services.Localization.UiText.Message("Text_BC2FA28989C5"));
    }
    private MountImportItem[] Requests() => _rows.Select(row => new MountImportItem(row.Configuration, row.LocalPath, row.Selected)).ToArray();
    private void ShowStatus(string message, InfoBarSeverity severity)
    { if (_closed || App.Current.IsExiting) return; StatusBar.Message = global::HybridMount.Services.Localization.UiText.Legacy(message); StatusBar.Severity = severity; StatusBar.IsOpen = true; }
    private void SetWorking(bool working)
    {
        _working = working; if (_closed || App.Current.IsExiting) return; Form.IsEnabled = !working && !_finished;
        UpdatePrimaryButton();
    }
    private async void SelectionChanged(object sender, RoutedEventArgs args)
    {
        if (_working || _applyingPreview || _finished) return;
        if (_importing) await ValidateRowsAsync(); else UpdatePrimaryButton();
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
            StatusBar.IsOpen = false;
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
                var password = ProtectionToggle.IsChecked == true ? PasswordField.Password : "";
                if (ProtectionToggle.IsChecked == true && password.Length < 8) throw new ArgumentException(global::HybridMount.Services.Localization.UiText.Text("BackupPasswordMinimum"));
                if (ProtectionToggle.IsChecked == true && !string.Equals(password, ConfirmPasswordField.Password, StringComparison.Ordinal)) throw new ArgumentException(global::HybridMount.Services.Localization.UiText.Text("Text_F007E1890A6A"));
                var ids = _rows.Where(row => row.Selected && row.CanSelect).Select(row => row.ExistingId!.Value).ToArray();
                if (ids.Length == 0) throw new ArgumentException(global::HybridMount.Services.Localization.UiText.Text("Text_DB1CDAA04D01"));
                var picker = new FileSavePicker { SuggestedFileName = $"HybridMount-config-{DateTime.Now:yyyyMMdd-HHmmss}" };
                picker.FileTypeChoices.Add(global::HybridMount.Services.Localization.UiText.Text("Text_4438ABDEDD78"), new List<string> { ".hmxbk" }); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindow!.Handle);
                var file = await picker.PickSaveFileAsync();
                if (_closed || App.Current.IsExiting) return;
                if (file is null) { ShowStatus(global::HybridMount.Services.Localization.UiText.Text("Text_B6C615F9C968"), InfoBarSeverity.Informational); return; }
                await App.Current.Mounts.ExportConfigurationsAsync(ids, password, file.Path, cancel.Token);
                ResultMessage = global::HybridMount.Services.Localization.UiText.Text("Text_BDC65C424B91",ids.Length); exported = true;
            }
            else if (!_decoded)
            {
                if (_readSession is null) return;
                AcceptDecoded(await App.Current.Mounts.DecodeConfigurationBackupAsync(_readSession, PasswordField.Password, cancel.Token));
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
                ResultMessage = global::HybridMount.Services.Localization.UiText.Text("Text_4EDAD650FAE2",ImportedIds.Count,skipped,failed,canceled);
                _finished = true; global::HybridMount.Helpers.UI.Ui.Set(this,"CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_3FD47EDCE45B"));
                ShowStatus(ResultMessage, failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException)
        { if (!App.Current.IsExiting) { ResultMessage = global::HybridMount.Services.Localization.UiText.Text("Text_068E55CEAFFC"); ShowStatus(ResultMessage, InfoBarSeverity.Informational); } }
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
        if (!_working) return; args.Cancel = true; _cancelRequested = true; global::HybridMount.Helpers.UI.Ui.Set(this,"CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_0ADDD7784578")); _active?.Cancel();
    }
    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    { _closed = true; _finished = true; _dialogStop.Cancel(); _active?.Cancel(); ++_previewVersion; PasswordField.Password = ""; ConfirmPasswordField.Password = ""; _rows.Clear(); _readSession?.Dispose(); _readSession = null; _localization?.Dispose(); _localization = null; _dialogStop.Dispose(); }
    /// <summary>UI线程退出时立即清除输入并释放快照租用；在途后台工作仍由应用等候，不越过停止状态提交。</summary>
    public void CancelForShutdown()
    {
        if (_closed) return;
        _cancelRequested = true;
        if (!_dialogStop.IsCancellationRequested) _dialogStop.Cancel();
        _active?.Cancel(); _readSession?.Dispose(); _readSession = null;
        PasswordField.Password = ""; ConfirmPasswordField.Password = "";
        if (!_working) Hide();
    }
}
