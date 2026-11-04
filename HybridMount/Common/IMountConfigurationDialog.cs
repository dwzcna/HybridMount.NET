using HybridMount.Models.Mounting;
namespace HybridMount.Common;

/// <summary>页面持有的配置视图生命周期；实现位于 Views，不依赖具体协议或另建页面模型。</summary>
public interface IMountConfigurationDialog : IDisposable
{
    MountDefinition? CreatedMount {get;}
    string ResultMessage {get;}
    /// <summary>退出只关闭视图和发出取消，网络任务仍由应用停止并等待。</summary>
    void CancelForShutdown();
}
