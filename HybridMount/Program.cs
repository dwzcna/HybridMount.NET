using HybridMount.Services.Mounting;
namespace HybridMount;

/// <summary>正常启动 WinUI；预览工作模式在独立 MTA 中解码，不创建应用、托盘、挂载或网络凭据。</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args is ["--preview-worker"])
        {
            var result=1;var thread=new Thread(()=>result=PreviewWorker.Run());thread.SetApartmentState(ApartmentState.MTA);thread.Start();thread.Join();return result;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(parameter=>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _=new App();
        });return 0;
    }
}
