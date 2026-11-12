using System.Globalization;
using HybridMount.Services.Localization;
namespace HybridMount.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly CloudOperationSettings? _cloud;
    private string _cloudConcurrency="4";
    private bool _savingCloud;
    private Task _cloudWork=Task.CompletedTask;
    public PageStatusMessage CloudMessage {get;}=new();
    public string CloudConcurrency {get=>_cloudConcurrency;set{if(!_disposed&&!_savingCloud)SetProperty(ref _cloudConcurrency,value??"");}}
    public bool CanSaveCloud=>CanEditSettings&&!_savingCloud&&_cloud is not null;
    public string CloudRecommendation=>UiText.Text("CloudRecommendation",CloudOperationSettings.Recommendation);
    /// <summary>UI只验证草稿与发布结果，注册表保存在线程池完成；页面释放后的迟到结果不再展示。</summary>
    public Task SaveCloudConcurrencyAsync()
    {
        if(!CanSaveCloud)return Task.CompletedTask;
        if(!int.TryParse(CloudConcurrency.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value is <1 or >128)
        {CloudMessage.Show(UiText.Message("CloudInvalid"),PageMessageSeverity.Error);return Task.CompletedTask;}
        _savingCloud=true;OnPropertyChanged(nameof(CanSaveCloud));return _cloudWork=SaveCloudAsync(value);
    }
    private async Task SaveCloudAsync(int value)
    {
        try
        {
            var saved=await Task.Run(()=>_cloud!.TrySave(value));
            if(!_disposed)CloudMessage.Show(UiText.Message(saved?"CloudSaved":"CloudSaveFailed"),saved?PageMessageSeverity.Success:PageMessageSeverity.Error);
        }
        finally{_savingCloud=false;if(!_disposed)OnPropertyChanged(nameof(CanSaveCloud));}
    }
}
