using CommunityToolkit.Mvvm.ComponentModel;

namespace HybridMount.ViewModels;

/// <summary>设置页整合外观、关闭行为与实际启动状态；失败回滚控件选择。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IStartupService _startup;
    private readonly LoggerService _logger;
    private bool _busy;
    private bool _closeToTray = true;
    private StartupState _startupState = new(false, true, "登录启动默认关闭。");
    private string _status = "";
    /// <summary>已经迁入的外观 ViewModel。</summary>
    public AppearanceSettingsViewModel Appearance { get; }
    /// <summary>初始化配置，缺失时关闭到托盘默认启用。</summary>
    public SettingsViewModel(AppearanceSettingsViewModel appearance, IStartupService startup, LoggerService logger)
    {
        Appearance = appearance; _startup = startup; _logger = logger;
        try { _closeToTray = WinRegisterService.ReadFlag("CloseToTray", true); }
        catch (Exception error) { _status = "关闭偏好读取失败，暂按隐藏到托盘处理。"; _logger.Queue("设置", "Warning", error.Message); }
    }
    /// <summary>实际关闭偏好，供窗口生命周期读取。</summary>
    public bool CloseToTray => _closeToTray;
    /// <summary>只有启动操作不在执行时才允许重复切换。</summary>
    public bool CanEditStartup => !_busy && _startupState.CanToggle;
    /// <summary>当前启动状态说明。</summary>
    public string StartupDetail => _startupState.Detail;
    /// <summary>操作结果或错误提示。</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    /// <summary>关闭到托盘开关；持久化成功后才发布。</summary>
    public bool CloseToTraySelection
    {
        get => _closeToTray;
        set
        {
            if (value == _closeToTray) return;
            try
            {
                WinRegisterService.WriteFlag("CloseToTray", value);
                _closeToTray = value;
                OnPropertyChanged(nameof(CloseToTray));
                Status = value ? "关闭窗口时将隐藏到托盘。" : "关闭窗口时将退出应用。";
                _logger.Queue("设置", "Information", Status);
            }
            catch (Exception error) { Status = "关闭偏好保存失败，已保留原值。"; _logger.Queue("设置", "Error", error.Message); }
            OnPropertyChanged();
        }
    }
    /// <summary>启动控件读取系统状态；候选值不作为实际结果。</summary>
    public bool StartupSelection
    {
        get => _startupState.Enabled;
        set { if (value != _startupState.Enabled && CanEditStartup) _ = ChangeStartupAsync(value); }
    }
    /// <summary>进入设置页重新检查系统状态，反映外部禁用或策略变化。</summary>
    public async Task RefreshStartupAsync()
    {
        if (_busy) return;
        _busy = true; NotifyStartup();
        try { _startupState = await _startup.GetAsync(); }
        catch (Exception error) { Status = "读取登录启动状态失败。"; _logger.Queue("启动", "Error", error.Message); }
        finally { _busy = false; NotifyStartup(); }
    }
    private async Task ChangeStartupAsync(bool enabled)
    {
        _busy = true; NotifyStartup();
        try { _startupState = await _startup.SetAsync(enabled); Status = _startupState.Detail; }
        catch (Exception error)
        {
            Status = "修改登录启动失败，正在核对系统实际状态。";
            _logger.Queue("启动", "Error", error.Message);
            try { _startupState = await _startup.GetAsync(); }
            catch (Exception queryError) { _logger.Queue("启动", "Warning", queryError.Message); }
        }
        finally { _busy = false; NotifyStartup(); }
    }
    private void NotifyStartup()
    {
        OnPropertyChanged(nameof(StartupSelection)); OnPropertyChanged(nameof(CanEditStartup)); OnPropertyChanged(nameof(StartupDetail));
    }
}
