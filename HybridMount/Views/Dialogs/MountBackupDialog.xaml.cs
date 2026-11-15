using HybridMount;
using HybridMount.Infrastructure.Configuration;
using HybridMount.Models.Configuration;
using HybridMount.Models.Localization;
using HybridMount.Models.Mounting;
using HybridMount.UI;
using HybridMount.UI.Localization;
using HybridMount.ViewModels;
using HybridMount.Views.Windows;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Storage.Pickers;

namespace HybridMount.Views.Dialogs;

/// <summary>导入只转交单个新建草稿，导出只转交应用任务；文件识别、解密、写入继续在后台。</summary>
public sealed partial class MountBackupDialog : ContentDialog
{
    private IDisposable? _localization;
    public BackupInteractionViewModel Interaction { get; } = new();
    private readonly bool _importing;
    private readonly string? _inputPath;
    private readonly ObservableCollection<MountBackupRowViewModel> _rows = [];
    private readonly CancellationTokenSource _dialogStop = new();
    private CancellationTokenSource? _active;
    private MountBackupReadSession? _readSession;
    private MountBackupItem? _selectedConfiguration;
    private bool _picking, _working, _decoded, _cancelRequested, _closed, _initialized, _loadStarted;
    public string ResultMessage { get; private set; } = "";
    public string? AcceptedTaskId { get; private set; }

    /// <summary>关闭后单次取走独立条目；清理对话框不会清除下一表单仍持有的不可变数据。</summary>
    internal MountBackupItem? TakeSelectedConfiguration()
    {
        var item = _selectedConfiguration; _selectedConfiguration = null; return item;
    }
    public MountBackupDialog(string? inputPath = null, bool protectionInitially = false)
    {
        _inputPath = inputPath; _importing = inputPath is not null;
        InitializeComponent();
        _localization = global::HybridMount.UI.UserInterface.ObserveLanguage(this, () => { Bindings.Update(); RefreshImportDetails(); });
        RowsList.ItemsSource = ImportChoice.ItemsSource = _rows;
        ProtectionToggle.Visibility = UnprotectedNotice.Visibility = _importing ? Visibility.Collapsed : Visibility.Visible;
        global::HybridMount.UI.UserInterface.Set(this, "Title", _importing ? global::HybridMount.Models.Localization.ResourceMessage.Create("ImportConfiguration") : global::HybridMount.Models.Localization.ResourceMessage.Create("ExportConfiguration"));
        global::HybridMount.UI.UserInterface.Set(this, "PrimaryButtonText", _importing ? global::HybridMount.Models.Localization.ResourceMessage.Create("DecryptAndPreview") : global::HybridMount.Models.Localization.ResourceMessage.Create("Export"));
        ConfirmPasswordField.Visibility = _importing ? Visibility.Collapsed : Visibility.Visible;
        RowsList.Visibility = _importing ? Visibility.Collapsed : Visibility.Visible;
        if (!_importing)
        {
            foreach (var mount in App.Current.Mounts.DisplayMounts.Where(item => item.Definition.Source?.Kind is FileSourceKind.WebDav or FileSourceKind.S3 or FileSourceKind.Synology))
            {
                var definition = mount.Definition;
                var row = new MountBackupRowViewModel(new(definition.Name, definition.LocalPath, definition.Source!, new(), definition.AutoConnect, definition.ThumbnailsEnabled), false, definition.Id);
                if (definition.RemovalPending) { row.Selected = false; row.CanSelect = false; row.SetStatus(global::HybridMount.Models.Localization.ResourceMessage.Create("MountsBeingRemovedCannotBeExportedYet")); }
                _rows.Add(row);
            }
            if (_rows.Count == 0) ShowStatus(global::HybridMount.Models.Localization.ResourceMessage.Create("ThereAreNoWebDAVS3OrSynologyMountsToExport"), InfoBarSeverity.Informational);
        }
        if (!_importing && protectionInitially)
        {
            ProtectionToggle.IsChecked = true; PasswordPanel.Visibility = Visibility.Visible; UnprotectedNotice.Visibility = Visibility.Collapsed;
            ShowStatus(global::HybridMount.Models.Localization.ResourceMessage.Create("BackupPasswordReentryRequired"), InfoBarSeverity.Informational);
        }
        Interaction.Publish(false, false, false);
    }
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
        finally { _active = null; SetWorking(false); if (_cancelRequested && !_closed) Hide(); }
    }
    private void ProtectionChanged(object sender, RoutedEventArgs args)
    {
        if (!_initialized || _importing || _closed) return;
        var enabled = ProtectionToggle.IsChecked == true;
        PasswordPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UnprotectedNotice.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (!enabled) PasswordField.Password = ConfirmPasswordField.Password = "";
        UpdatePrimaryButton();
    }
    private void PasswordChanged(object sender, RoutedEventArgs args) { if (_initialized && !_closed) UpdatePrimaryButton(); }
    private void UpdatePrimaryButton()
    {
        if (_closed || App.Current.IsExiting) return;
        var valid = _importing ? _decoded ? ImportChoice.SelectedItem is MountBackupRowViewModel : _readSession?.RequiresPassword == true && PasswordField.Password.Length >= 8 : _rows.Any(row => row.Selected && row.CanSelect);
        if (!_importing && ProtectionToggle.IsChecked == true) valid &= PasswordField.Password.Length >= 8 && PasswordField.Password == ConfirmPasswordField.Password;
        Interaction.Publish(valid, _working, _closed);
    }
    /// <summary>解码不校验或禁用本机目录；任何条目都可进入草稿，只有最终提交才校验真实路径。</summary>
    private void AcceptDecoded(MountConfigurationBackup document)
    {
        if (_closed || App.Current.IsExiting || _dialogStop.IsCancellationRequested) return;
        foreach (var item in document.Mounts) _rows.Add(new MountBackupRowViewModel(item, true) { Selected = false });
        _decoded = true; _readSession?.Dispose(); _readSession = null;
        PasswordField.Password = ConfirmPasswordField.Password = ""; PasswordPanel.Visibility = Visibility.Collapsed;
        RowsList.Visibility = Visibility.Collapsed; ImportSelectionPanel.Visibility = Visibility.Visible; ImportChoice.SelectedIndex = -1;
        global::HybridMount.UI.UserInterface.Set(this, "PrimaryButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("BackupContinueConfiguration"));
        UpdatePrimaryButton();
    }
    private void ImportChoiceChanged(object sender, SelectionChangedEventArgs args) { if (!_closed) { RefreshImportDetails(); UpdatePrimaryButton(); } }
    private void RefreshImportDetails()
    {
        if (_closed) return;
        ImportDetails.Text = ImportChoice.SelectedItem is MountBackupRowViewModel row ? row.SourceDisplay + "\n" + row.LocalPath + "\n" + row.OptionsDisplay : "";
    }
    private void SelectionChanged(object sender, RoutedEventArgs args) { if (!_working && !_closed) UpdatePrimaryButton(); }
    private void ShowStatus(string text, InfoBarSeverity severity) => ShowStatus(global::HybridMount.Models.Localization.ResourceMessage.Raw(text), severity);
    private void ShowStatus(global::HybridMount.Models.Localization.ResourceMessage text, InfoBarSeverity severity)
    {
        if (_closed || App.Current.IsExiting) return;
        global::HybridMount.UI.UserInterface.Set(StatusBar, "Message", text); StatusBar.Severity = severity; StatusBar.IsOpen = true;
    }
    private void SetWorking(bool value) { _working = value; if (_closed || App.Current.IsExiting) return; UpdatePrimaryButton(); }
    /// <summary>转交仅复制已选条目；无选择时不生成草稿，不读写配置或远端。</summary>
    private bool CaptureImportSelection()
    {
        if (!_decoded || ImportChoice.SelectedItem is not MountBackupRowViewModel row) return false;
        _selectedConfiguration = row.Configuration with { ExclusionRules = Array.AsReadOnly((row.Configuration.ExclusionRules ?? []).ToArray()) };
        return true;
    }
    private async Task DecodeImportAsync(string password, CancellationToken token)
    {
        if (_readSession is not null) AcceptDecoded(await App.Current.Mounts.DecodeConfigurationBackupAsync(_readSession, password, token));
    }
    private async void Submit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; if (_working || _picking || _closed) return;
        var deferral = args.GetDeferral(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_dialogStop.Token); _active = cancel;
        try
        {
            if (_importing && _decoded)
            {
                args.Cancel = !CaptureImportSelection();
            }
            else if (_importing)
            {
                SetWorking(true);
                await DecodeImportAsync(PasswordField.Password, cancel.Token);
            }
            else
            {
                var password = ProtectionToggle.IsChecked == true ? PasswordField.Password : "";
                if (ProtectionToggle.IsChecked == true && password.Length < 8) throw new ArgumentException(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("BackupPasswordMinimum", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);
                if (ProtectionToggle.IsChecked == true && password != ConfirmPasswordField.Password) throw new ArgumentException(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TheTwoBackupPasswordsDoNotMatch", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);
                var ids = _rows.Where(row => row.Selected && row.CanSelect).Select(row => row.ExistingId!.Value).ToArray();
                if (ids.Length == 0) throw new ArgumentException(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SelectTheMountsToExport", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);
                var picker = new FileSavePicker { SuggestedFileName = $"HybridMount-config-{DateTime.Now:yyyyMMdd-HHmmss}" };
                picker.FileTypeChoices.Add(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("HybridMountJSONConfigurationBackup", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, new List<string> { ".hmxbk" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindow!.Handle);
                _picking = true;
                var file = await picker.PickSaveFileAsync().AsTask(cancel.Token);
                _picking = false;
                if (_closed || App.Current.IsExiting) return;
                if (file is null) { ShowStatus(global::HybridMount.Models.Localization.ResourceMessage.Create("FileSelectionCanceled"), InfoBarSeverity.Informational); return; }
                cancel.Token.ThrowIfCancellationRequested();
                SetWorking(true);
                var accepted = await App.Current.Mounts.AcceptConfigurationExportAsync(ids, password, file.Path, cancel.Token);
                AcceptedTaskId = accepted.TaskId;
                ResultMessage = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("BackupExportAccepted", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
                args.Cancel = false;
            }
        }
        catch (OperationCanceledException) { if (!_closed && !App.Current.IsExiting) ShowStatus(global::HybridMount.Models.Localization.ResourceMessage.Create("ConfigurationSubmissionCanceled"), InfoBarSeverity.Informational); }
        catch (Exception error) { if (!_closed && !App.Current.IsExiting) ShowStatus(error.Message, InfoBarSeverity.Error); }
        finally { _picking = false; _active = null; SetWorking(false); deferral.Complete(); if (_cancelRequested && !_closed && !App.Current.IsExiting) Hide(); }
    }
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_working && !_picking) return;
        args.Cancel = true; _cancelRequested = true;
        global::HybridMount.UI.UserInterface.Set(this, "CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("Canceling")); _active?.Cancel();
    }
    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _closed = true; Interaction.Publish(false, false, true); _dialogStop.Cancel(); _active?.Cancel();
        PasswordField.Password = ConfirmPasswordField.Password = ""; _rows.Clear();
        _readSession?.Dispose(); _readSession = null; _localization?.Dispose(); _localization = null; _dialogStop.Dispose();
    }
    public void CancelForShutdown()
    {
        if (_closed) return;
        _cancelRequested = true; _dialogStop.Cancel(); _active?.Cancel(); _selectedConfiguration = null;
        _readSession?.Dispose(); _readSession = null; PasswordField.Password = ConfirmPasswordField.Password = "";
        if (!_working && !_picking) Hide();
    }
}
