using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models;
using HybridMount.Services;


namespace HybridMount.ViewModels;

/// <summary>设置页双向外观VM；未持久成功前 getter 始终返回最后有效值，失败通知回滚控件。</summary>
public sealed class AppearanceSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppearanceSettingsService _settings;
    private bool _saving, _disposed, _notifyingSelections;
    private string _status;
    public AppearanceSettingsViewModel(AppearanceSettingsService settings)
    {
        _settings = settings; _status = settings.ErrorStatus ?? "";
        settings.Changed += SettingsChanged;
    }
    public bool CanEdit => !_disposed && _settings.IsLoaded && !_saving;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    /// <summary>ComboBox稳定Tag；未知值和加载期间的空选择不回写默认。</summary>
    public string BackdropSelection
    {
        get => _settings.Current.Backdrop.ToString();
        set
        {
            // <remarks>Source→Target→Source同步回填相同值直接结束；通知／保存期间拒绝重入，不再次通知。</remarks>
            if (value == BackdropSelection || _disposed || _saving || _notifyingSelections) return;
            if (CanEdit && AppearancePreferences.TryBackdrop(value, out var backdrop))
                Save(_settings.Current with { Backdrop = backdrop });
            else NotifySelections();
        }
    }
    public string ThemeSelection
    {
        get => _settings.Current.Theme.ToString();
        set
        {
            if (value == ThemeSelection || _disposed || _saving || _notifyingSelections) return;
            if (CanEdit && AppearancePreferences.TryTheme(value, out var theme))
                Save(_settings.Current with { Theme = theme });
            else NotifySelections();
        }
    }
    private void Save(AppearancePreferences candidate)
    {
        _saving = true;
        try
        {
            OnPropertyChanged(nameof(CanEdit));
            Status = _settings.TrySave(candidate, out var error) ? "外观设置已保存，立即生效。" : error ?? "外观设置保存失败。";
        }
        finally { _saving = false; NotifySelections(); }
    }
    /// <summary>进入设置页重读正式偏好；失败保留有效选择并展示固定错误。</summary>
    public void Reload()
    {
        if (_disposed || _saving || _notifyingSelections) return;
        _saving = true;
        try { if (!_settings.Reload()) Status = _settings.ErrorStatus ?? "外观设置读取失败。"; }
        finally { _saving = false; NotifySelections(); }
    }
    private void SettingsChanged(object? sender, EventArgs args)
    { if (!_disposed && !_saving) NotifySelections(); }
    /// <summary>统一有限回滚通知；xBind同步回填期间只读getter，不再次写入或触发通知。</summary>
    /// <remarks>标记覆盖Source→Target→Source闭环；异常也在finally清除，不永久锁住编辑。</remarks>
    private void NotifySelections()
    {
        if (_disposed || _notifyingSelections) return;
        _notifyingSelections = true;
        try
        {
            OnPropertyChanged(nameof(BackdropSelection));
            OnPropertyChanged(nameof(ThemeSelection));
            OnPropertyChanged(nameof(CanEdit));
        }
        finally { _notifyingSelections = false; }
    }
    /// <summary>应用退出解除纯设置订阅；不持有窗口或对话框根。</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _settings.Changed -= SettingsChanged; }
}
