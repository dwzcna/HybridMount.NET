using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Shell;
using HybridMount.Infrastructure.Windows;
using System.Runtime.InteropServices;
namespace HybridMount;

/// <summary>普通入口启动 WinUI；维护及预览入口不创建应用、托盘、挂载或网络凭据。</summary>
internal static class Program
{
    [DllImport("user32.dll", EntryPoint="MessageBoxW", CharSet=CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string title, uint type);
    [STAThread]
    private static int Main(string[] args)
    {
        // 维护入口必须先于 WinUI、单实例、托盘和任何挂载服务；无有效私有握手时立即退出。
        if(ShellMaintenanceProcess.IsMaintenanceEntry()||args.Length>0&&args[0]=="--shell-maintenance")return HybridMount.Infrastructure.Diagnostics.HostDiagnosticStore.Run("ShellMaintenance",()=>ShellMaintenanceProcess.Run(args));
        if(ThumbnailServer.IsEntry(args))return 2; // COM入口只由HybridMount.Stock.exe承接。
        if(args is ["--preview-worker"])return 2; // 解码只由纯C++隔离工作进程承接。
        try { PackageStartupGuard.Validate(); }
        catch (Exception error)
        {
            var resources = new System.Resources.ResourceManager("HybridMount.Resources.Messages", typeof(Program).Assembly);
            var key = error is PackageStartupConflictException ? "PreviousPackageConflict" : "PackageIdentityCheckFailed";
            MessageBox(0, resources.GetString(key, System.Globalization.CultureInfo.CurrentUICulture) ?? key, "HybridMount.NET", 0x30);
            return 4;
        }
        return HybridMount.Infrastructure.Diagnostics.HostDiagnosticStore.Run("Startup",()=>
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
