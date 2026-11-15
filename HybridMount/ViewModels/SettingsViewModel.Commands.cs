using CommunityToolkit.Mvvm.Input;
using HybridMount.Models.UI;
using System.ComponentModel;

namespace HybridMount.ViewModels;

public sealed partial class SettingsViewModel
{
    private Func<SettingsDialogKind,Task>? _dialogPresenter;
    private bool _commandsReady;
    private bool[] _commandValues=[];
    public Task ModalCommandTask {get;private set;}=Task.CompletedTask;
    public IAsyncRelayCommand SaveCloudCommand {get;private set;}=null!;
    public IRelayCommand SaveCacheCommand {get;private set;}=null!;
    public IAsyncRelayCommand TrimCacheCommand {get;private set;}=null!;
    public IRelayCommand ResetCommand {get;private set;}=null!;
    public IRelayCommand RemoveAllCommand {get;private set;}=null!;
    private void InitializeCommands()
    {
        SaveCloudCommand=new AsyncRelayCommand(SaveCloudConcurrencyAsync,()=>CanSaveCloud);
        SaveCacheCommand=new RelayCommand(SaveCacheLimit,()=>CanEditSettings&&CanEditCache&&!_savingCache);
        TrimCacheCommand=new AsyncRelayCommand(TrimCacheAsync,()=>CanEditSettings&&CanEditCache);
        ResetCommand=new RelayCommand(()=>RequestDialog(SettingsDialogKind.Reset),()=>CanReset);
        RemoveAllCommand=new RelayCommand(()=>RequestDialog(SettingsDialogKind.RemoveAll),()=>CanRemoveAll);
        _commandValues=CommandValues();_commandsReady=true;
    }
    /// <summary>视图负责确认／窗口归属；本模型保留完整流程Task，内部占用不参与CanExecute。</summary>
    public void SetDialogPresenter(Func<SettingsDialogKind,Task>? presenter)=>_dialogPresenter=presenter;
    private void RequestDialog(SettingsDialogKind kind)
    {
        if(_resetFlow||_dialogPresenter is not {} presenter||!(kind==SettingsDialogKind.Reset?CanReset:CanRemoveAll))return;
        ModalCommandTask=PresentAsync();
        async Task PresentAsync()
        {
            try{await presenter(kind);}
            catch(OperationCanceledException){}
            catch(Exception error){if(!_disposed)ResetMessage.Show(error.Message,PageMessageSeverity.Error);}
        }
    }
    private bool[] CommandValues()=>[CanSaveCloud,CanEditSettings&&CanEditCache&&!_savingCache,CanEditSettings&&CanEditCache,CanReset,CanRemoveAll];
    protected override void OnPropertyChanged(PropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if(!_commandsReady)return;
        var values=CommandValues();
        IRelayCommand[] commands=[SaveCloudCommand,SaveCacheCommand,TrimCacheCommand,ResetCommand,RemoveAllCommand];
        for(var index=0;index<values.Length;index++)if(_commandValues[index]!=values[index]){_commandValues[index]=values[index];commands[index].NotifyCanExecuteChanged();}
    }
}
