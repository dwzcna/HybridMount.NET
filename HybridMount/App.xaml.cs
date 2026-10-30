using HybridMount.Services.Mounting;
using HybridMount.ViewModels;
using HybridMount.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;

namespace HybridMount;

/// <summary>同进程后台提供 CFAPI，主窗口按需创建；双协议管道只接受当前用户会话。</summary>
public partial class App:Application
{
    private SingleInstanceService? _instance,_legacyInstance;
    private TrayIconService? _tray;
    private IpcServerService? _ipc,_legacyIpc;
    private DispatcherQueue? _dispatcher;
    private bool _exiting,_allowClose;
    public new static App Current=>(App)Application.Current;
    public static string Version=>System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion.Split('+')[0]??typeof(App).Assembly.GetName().Version?.ToString()??"未知版本";
    public MainWindow? MainWindow{get;private set;}
    public LoggerService Logger{get;private set;}=null!;
    public AppearanceSettingsService Appearance{get;private set;}=null!;
    public SettingsViewModel Settings{get;private set;}=null!;
    public MountManager Mounts{get;private set;}=null!;
    public App()=>InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher=DispatcherQueue.GetForCurrentThread();
        try
        {
            var silent=Environment.GetCommandLineArgs().Any(value=>value is "--tray" or "--background")||AppInstance.GetCurrent().GetActivatedEventArgs().Kind==ExtendedActivationKind.StartupTask;
            _instance=new SingleInstanceService();
            _legacyInstance=new SingleInstanceService($"Local\\{IpcProtocol.LegacyPipeName}.Instance");
            if(!_instance.IsPrimary||!_legacyInstance.IsPrimary)
            {
                var request=new IpcRequest(2,Guid.NewGuid().ToString(),silent?"ping":"showWindow",[]);
                try{await IpcClient.SendAsync(request);}
                catch{try{await IpcClient.SendAsync(request with{Version=1},IpcProtocol.LegacyPipeName);}catch(Exception error){if(!silent)MessageBox(0,$"已有实例未响应，请退出旧实例后重试。\n{error.Message}","HybridMount.NET",0x10);}}
                _legacyInstance.Dispose();_instance.Dispose();_legacyInstance=null;_instance=null;Exit();return;
            }
            Logger=new LoggerService();Appearance=new AppearanceSettingsService(new AppearancePreferenceStoreService());
            Settings=new SettingsViewModel(new AppearanceSettingsViewModel(Appearance),new StartupService(Logger),Logger);
            Mounts=new MountManager(new MountRepository(Path.Combine(ApplicationPaths.DataDirectory,"Mounting")),Logger);
            Appearance.Changed+=OnAppearanceChanged;Logger.NotificationRequested+=OnNotification;
            try{_tray=new TrayIconService();_tray.RestoreRequested+=RestoreWindow;_tray.ExitRequested+=RequestExit;}
            catch(Exception error){Logger.Queue("托盘","Error",error.Message);}
            _ipc=new IpcServerService(Logger){Handler=request=>HandleRequest(request,false)};_ipc.Start();
            _legacyIpc=new IpcServerService(Logger,IpcProtocol.LegacyPipeName){Handler=request=>HandleRequest(request,true)};_legacyIpc.Start();
            _=Mounts.StartAsync();
            Logger.Queue("应用","Information",$"HybridMount.NET {Version} 已启动。");
            if(!silent||_tray?.IsRegistered!=true)RestoreWindow();
        }
        catch(Exception error){MessageBox(0,$"HybridMount.NET 启动失败：\n{error.Message}","HybridMount.NET",0x10);await ExitAsync();}
    }
    private IpcResponse HandleRequest(IpcRequest request,bool legacy)
    {
        var version=legacy?1:2;
        if((legacy&&request.Version!=1)||!IpcProtocol.IsValid(request with{Version=legacy?2:request.Version}))return new(version,request.RequestId??"","invalidRequest","请求无效。");
        if(request.Operation=="ping")return new(version,request.RequestId,"ready","HybridMount.NET 已运行。");
        if(request.Operation=="showWindow") {_dispatcher?.TryEnqueue(RestoreWindow);return new(version,request.RequestId,"accepted","已请求显示窗口。");}
        if(legacy)return new(1,request.RequestId,"notImplemented","旧版菜单未执行文件操作，请更新扩展后重试。");
        try
        {
            if(request.Operation=="getPreview")
            {
                var png=Mounts.GetPreview(request.Paths[0],request.PreviewSize!.Value);
                return new(2,request.RequestId,png is null?"notFound":"ok","",PreviewPng:png);
            }
            if(request.Operation=="getOperation")
            {
                var job=Mounts.GetOperation(request.JobId!);
                return new(2,request.RequestId,job is null?"notFound":"ok","任务结果见任务页。",request.JobId,job is null?null:job with{Results=job.Results.Take(3).ToArray()});
            }
            if(request.Operation=="cancelOperation")return new(2,request.RequestId,Mounts.Cancel(request.JobId!)?"accepted":"notFound","已请求取消，已完成步骤保留。",request.JobId);
            var mount=Mounts.FindMount(request.Paths);var id=Mounts.Queue(mount,request.Operation,request.Paths);
            return new(2,request.RequestId,"accepted","操作已提交，可在任务页查看逐项结果。",id);
        }
        catch(Exception error){if(request.Operation!="getPreview")Logger.Queue("资源管理器","Warning",error.Message);return new(2,request.RequestId,"rejected",request.Operation=="getPreview"?"":error.Message);}
    }
    private void OnAppearanceChanged(object? sender,EventArgs args)=>MainWindow?.ApplyAppearance();
    private void OnNotification(object? sender,LogNotificationEventArgs args)=>MainWindow?.ShowNotification(args);
    public void RestoreWindow()
    {
        if(_exiting)return;
        if(MainWindow is null){MainWindow=new MainWindow();MainWindow.AppWindow.Closing+=OnWindowClosing;}
        MainWindow.AppWindow.Show();ShowWindow(MainWindow.Handle,9);MainWindow.Activate();SetForegroundWindow(MainWindow.Handle);
    }
    private void OnWindowClosing(AppWindow sender,AppWindowClosingEventArgs args)
    {
        if(_allowClose)return;args.Cancel=true;
        if(!_exiting&&Settings.CloseToTray&&_tray?.IsRegistered==true)sender.Hide();else RequestExit();
    }
    private async void RequestExit()=>await ExitAsync();
    private async Task ExitAsync()
    {
        if(_exiting)return;_exiting=true;
        try
        {
            if(_ipc is not null)await _ipc.DisposeAsync();if(_legacyIpc is not null)await _legacyIpc.DisposeAsync();
            if(Mounts is not null)await Mounts.DisposeAsync();
        }
        catch(Exception error){Logger?.Queue("退出","Error",error.Message);}
        finally
        {
            if(Appearance is not null)Appearance.Changed-=OnAppearanceChanged;
            if(Settings is not null)Settings.Appearance.Dispose();MainWindow?.ReleaseResources();_tray?.Dispose();
            if(Logger is not null){Logger.NotificationRequested-=OnNotification;Logger.Queue("应用","Information","HybridMount.NET 正在退出。");await Logger.DisposeAsync();}
            _legacyInstance?.Dispose();_instance?.Dispose();_allowClose=true;MainWindow?.Close();MainWindow=null;Exit();
        }
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBox(nint owner,string text,string caption,uint type);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
}
