using CommunityToolkit.Mvvm.ComponentModel;

namespace HybridMount.ViewModels;

/// <summary>备份原生主按钮与输入组的唯一可用状态；文件选择器本身不改变此投影，不持有密码。</summary>
public sealed class BackupInteractionViewModel : ObservableObject
{
    private bool _canSubmit, _canEdit = true;
    public bool CanSubmit => _canSubmit;
    public bool CanEdit => _canEdit;
    public void Publish(bool valid, bool working, bool closed)
    {
        SetProperty(ref _canSubmit, valid && !working && !closed, nameof(CanSubmit));
        SetProperty(ref _canEdit, !working && !closed, nameof(CanEdit));
    }
}
