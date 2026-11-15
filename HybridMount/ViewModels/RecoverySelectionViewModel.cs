using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HybridMount.ViewModels;

/// <summary>遗留目录选择的局部状态；模态占用只防重入，真实身份检查才限制冲突选择。</summary>
public sealed class RecoverySelectionViewModel : ObservableObject, IDisposable
{
    private bool _choosing, _inspecting, _disposed, _eligible;
    private int _selected;
    private Func<Task>? _choose;
    private Action? _selectAll, _clearAll;
    private Action<Exception>? _error;
    public bool CanEdit => !_disposed && !_inspecting;
    public bool CanConfirm => CanEdit && _selected > 0;
    public bool IsChoosing => _choosing;
    public Task WhenIdle {get;private set;}=Task.CompletedTask;
    public IRelayCommand ChooseCommand {get;}
    public IRelayCommand SelectAllCommand {get;}
    public IRelayCommand ClearAllCommand {get;}
    public RecoverySelectionViewModel()
    {
        ChooseCommand=new RelayCommand(()=>{if(!CanEdit||_choosing||_choose is null)return;_choosing=true;WhenIdle=ChooseAsync();},()=>CanEdit);
        SelectAllCommand=new RelayCommand(()=>{if(CanEdit&&_eligible)_selectAll?.Invoke();},()=>CanEdit&&_eligible);
        ClearAllCommand=new RelayCommand(()=>{if(CanEdit&&_selected>0)_clearAll?.Invoke();},()=>CanEdit&&_selected>0);
    }
    public void SetHandlers(Func<Task> choose,Action selectAll,Action clearAll,Action<Exception> error){_choose=choose;_selectAll=selectAll;_clearAll=clearAll;_error=error;}
    public void UpdateSelection(int selected,bool eligible) => Update(()=>{_selected=selected;_eligible=eligible;});
    public void SetInspecting(bool inspecting)=>Update(()=>_inspecting=inspecting);
    private void Update(Action apply)
    {
        if(_disposed)return;
        var edit=CanEdit;var confirm=CanConfirm;var select=SelectAllCommand.CanExecute(null);var clear=ClearAllCommand.CanExecute(null);
        apply();
        if(edit!=CanEdit){OnPropertyChanged(nameof(CanEdit));ChooseCommand.NotifyCanExecuteChanged();}
        if(confirm!=CanConfirm)OnPropertyChanged(nameof(CanConfirm));
        if(select!=SelectAllCommand.CanExecute(null))SelectAllCommand.NotifyCanExecuteChanged();
        if(clear!=ClearAllCommand.CanExecute(null))ClearAllCommand.NotifyCanExecuteChanged();
    }
    private async Task ChooseAsync(){try{await _choose!();}catch(OperationCanceledException){}catch(Exception error){if(!_disposed)_error?.Invoke(error);}finally{_choosing=false;}}
    public void Dispose(){if(_disposed)return;SetInspecting(true);_disposed=true;_choose=null;_selectAll=null;_clearAll=null;_error=null;}
}
