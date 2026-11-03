namespace HybridMount.ViewModels;

/// <summary>首页唯一展示状态，不持有窗口；导航事件由页面适配，下一版统一导航入口。</summary>
public sealed class HomeViewModel(string version)
{
    public string VersionText=>"版本 "+version;
}
