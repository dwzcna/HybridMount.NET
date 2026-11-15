using CommunityToolkit.Mvvm.Input;
using HybridMount.Models.UI;

namespace HybridMount.ViewModels;

public sealed partial class MountsViewModel
{
    private Func<MountDialogKind, Task>? _dialogPresenter;
    /// <summary>视图仅提供展示回调；任务由本模型保留，模态占用不进入命令可用性。</summary>
    public Task ModalCommandTask { get; private set; } = Task.CompletedTask;
    public bool CanOpenDialog => !IsStopping && !_runtimeReconnecting;
    public IRelayCommand CreateWebDavCommand { get; private set; } = null!;
    public IRelayCommand CreateS3Command { get; private set; } = null!;
    public IRelayCommand CreateSynologyCommand { get; private set; } = null!;
    public IRelayCommand ImportConfigurationsCommand { get; private set; } = null!;
    public IRelayCommand ExportConfigurationsCommand { get; private set; } = null!;
    public IRelayCommand RecoverMountsCommand { get; private set; } = null!;
    private bool _publishedCanOpenDialog;
    private void InitializePageCommands()
    {
        IRelayCommand Command(MountDialogKind kind) => new RelayCommand(() => RequestDialog(kind), () => CanOpenDialog);
        CreateWebDavCommand = Command(MountDialogKind.NewWebDav);
        CreateS3Command = Command(MountDialogKind.NewS3);
        CreateSynologyCommand = Command(MountDialogKind.NewSynology);
        ImportConfigurationsCommand = Command(MountDialogKind.Import);
        ExportConfigurationsCommand = Command(MountDialogKind.Export);
        RecoverMountsCommand = Command(MountDialogKind.Recovery);
        _publishedCanOpenDialog = CanOpenDialog;
    }
    /// <summary>页面创建／释放时接入或解除纯展示桥；不保存任何WinUI对象。</summary>
    public void SetDialogPresenter(Func<MountDialogKind, Task>? presenter) => _dialogPresenter = presenter;
    private void RequestDialog(MountDialogKind kind)
    {
        if (!CanOpenDialog || _dialogBusy || _dialogPresenter is not { } presenter) return;
        // RunDialogAsync在返回首个不完整Task前同步取得占用；本字段持有并观察整条流程。
        ModalCommandTask = RunDialogAsync(() => presenter(kind));
    }
    private void NotifyPageCommands()
    {
        var current = CanOpenDialog;
        if (_publishedCanOpenDialog == current) return;
        _publishedCanOpenDialog = current;
        OnPropertyChanged(nameof(CanOpenDialog));
        foreach (var command in new[] { CreateWebDavCommand, CreateS3Command, CreateSynologyCommand, ImportConfigurationsCommand, ExportConfigurationsCommand, RecoverMountsCommand })
            command.NotifyCanExecuteChanged();
    }
}
