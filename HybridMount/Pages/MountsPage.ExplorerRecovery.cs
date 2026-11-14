using HybridMount.Controls.Mounting;

namespace HybridMount.Pages;

public sealed partial class MountsPage
{
    private ExplorerEntryDialog? _explorerRecovery;
    private async Task RecoverExplorerEntriesAsync()
    {
        if(_explorerRecovery is not null)return;
        using var dialog=new ExplorerEntryDialog(this,()=>_disposed||ViewModel.IsStopping);
        _explorerRecovery=dialog;
        try{await dialog.ShowAsync();}
        finally{_explorerRecovery=null;}
    }
}
