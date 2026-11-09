using HybridMount.Services.Mounting;
namespace HybridMount;

/// <summary>普通入口启动 WinUI；维护及预览入口不创建应用、托盘、挂载或网络凭据。</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 维护入口必须先于 WinUI、单实例、托盘和任何挂载服务；无有效私有握手时立即退出。
        if(ShellMaintenanceProcess.IsMaintenanceEntry()||args.Length>0&&args[0]=="--shell-maintenance")return Services.HostDiagnosticStore.Run("ShellMaintenance",()=>ShellMaintenanceProcess.Run(args));
        if(ThumbnailServer.IsEntry(args))return 2; // COM入口只由HybridMount.Native.exe承接。
        if(args is ["--preview-worker"])return 2; // 解码只由纯C++隔离工作进程承接。
        return Services.HostDiagnosticStore.Run("Startup",()=>
        {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(parameter=>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _=new App();
        });return 0;
        });
    }
}
