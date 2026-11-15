using HybridMount.Models.UI;
using HybridMount.Models.Localization;
using HybridMount.Services.Settings;
using HybridMount.UI.Localization;
using System.Globalization;
namespace HybridMount.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly CloudOperationSettings? _cloud;
    private string _cloudConcurrency = "4";
    private bool _savingCloud;
    private Task _cloudWork = Task.CompletedTask;
    public PageStatusViewModel CloudMessage { get; } = new();
    public string CloudConcurrency { get => _cloudConcurrency; set { if (!_disposed && !_savingCloud) if(SetProperty(ref _cloudConcurrency, value ?? ""))OnPropertyChanged(nameof(CloudConcurrencyValue)); } }
    /// <summary>NumberBox以数值绑定，区域格式由控件处理；空值、非整数仍由保存入口拒绝。</summary>
    public double CloudConcurrencyValue
    {
        get=>double.TryParse(_cloudConcurrency,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)?value:double.NaN;
        set=>CloudConcurrency=double.IsFinite(value)?value.ToString("R",CultureInfo.InvariantCulture):"";
    }
    public bool CanSaveCloud => CanEditSettings && !_savingCloud && _cloud is not null;
    public string CloudRecommendation => string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CloudRecommendation", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, CloudOperationSettings.Recommendation);
    /// <summary>UI只验证草稿与发布结果，注册表保存在线程池完成；页面释放后的迟到结果不再展示。</summary>
    public Task SaveCloudConcurrencyAsync()
    {
        if (!CanSaveCloud) return Task.CompletedTask;
        if (!int.TryParse(CloudConcurrency.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 128)
        { CloudMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Create("CloudInvalid"), PageMessageSeverity.Error); return Task.CompletedTask; }
        _savingCloud = true; OnPropertyChanged(nameof(CanSaveCloud)); return _cloudWork = SaveCloudAsync(value);
    }
    private async Task SaveCloudAsync(int value)
    {
        try
        {
            var saved = await Task.Run(() => _cloud!.TrySave(value));
            if (!_disposed) CloudMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Create(saved ? "CloudSaved" : "CloudSaveFailed"), saved ? PageMessageSeverity.Success : PageMessageSeverity.Error);
        }
        finally { _savingCloud = false; if (!_disposed) OnPropertyChanged(nameof(CanSaveCloud)); }
    }
}
