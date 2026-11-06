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
    private volatile bool _exiting;
    private bool _allowClose;
    private readonly object _exitSync=new();
    private Task? _exitTask;
    public bool IsExiting=>_exiting;
    private LifecycleDiagnostics? _lifecycle;
    public new static App Current=>(App)Application.Current;
    public static string Version=>System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion.Split('+')[0]??typeof(App).Assembly.GetName().Version?.ToString()??"未知版本";
    public NavigationService Navigation{get;}=new();
    public MainWindow? MainWindow{get;private set;}
    public LoggerService Logger{get;private set;}=null!;
    public AppearanceSettingsService Appearance{get;private set;}=null!;
    public ApplicationPreferencesService Preferences{get;private set;}=null!;
    public IStartupService Startup{get;private set;}=null!;
    public IUiDispatcher UiDispatcher{get;private set;}=null!;
    public TaskFocusService TaskFocus{get;private set;}=null!;
    public UploadIssueNotificationService IssueNotifications{get;private set;}=null!;
    public MountManager Mounts{get;private set;}=null!;
    public App()=>InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher=DispatcherQueue.GetForCurrentThread();UiDispatcher=new UiDispatcher(_dispatcher);
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
            _lifecycle=new LifecycleDiagnostics(ApplicationPaths.LogsDirectory,Version,WinRegisterService.IsPackaged()?"Packaged":"Unpackaged");
            UnhandledException+=OnUiUnhandled;AppDomain.CurrentDomain.UnhandledException+=OnDomainUnhandled;TaskScheduler.UnobservedTaskException+=OnTaskUnobserved;
            Logger=new LoggerService();
            if(_lifecycle.PreviousSessionIncomplete)Logger.Queue("生命周期","Warning","上次会话缺少退出完成记录，原因待定位；请查看 Logs 中的 lifecycle 诊断。该记录不单独证明发生崩溃。");
            Appearance=new AppearanceSettingsService();
            Preferences=new ApplicationPreferencesService(Logger);Startup=new StartupService(Logger);
            Mounts=new MountManager(new MountRepository(Path.Combine(ApplicationPaths.DataDirectory,"Mounting")),Logger){StopObserver=RecordMountStop};
            TaskFocus=new(Navigation);IssueNotifications=new(Mounts);IssueNotifications.Changed+=OnUploadIssueNotifications;
            Appearance.Changed+=OnAppearanceChanged;Logger.NotificationRequested+=OnNotification;
            try{_tray=new TrayIconService();_tray.RestoreRequested+=RestoreWindow;_tray.ExitRequested+=RequestExit;}
            catch(Exception error){Logger.Queue("托盘","Error",error.Message);}
            _ipc=new IpcServerService(Logger){Handler=request=>HandleRequest(request,false)};_ipc.Start();
            _legacyIpc=new IpcServerService(Logger,IpcProtocol.LegacyPipeName){Handler=request=>HandleRequest(request,true)};_legacyIpc.Start();
            _=Mounts.StartAsync();
            Logger.Queue("应用","Information",$"HybridMount.NET {Version} 已启动。");
            if(!silent||_tray?.IsRegistered!=true)RestoreWindow();
            _lifecycle.Record("Ready");
        }
        catch(Exception error){_lifecycle?.Record("StartupFailed",error,true);MessageBox(0,$"HybridMount.NET 启动失败：\n{error.Message}","HybridMount.NET",0x10);await ExitAsync();}
    }
    private void RecordMountStop(string phase,Exception? error)
    {
        var reason=phase.StartsWith("ConnectionCanceled.",StringComparison.Ordinal)?phase["ConnectionCanceled.".Length..]:_exiting?"Shutdown":null;
        _lifecycle?.Record("Mounts."+phase,error,cancellationReason:reason);
    }
    /// <summary>只记录未处理异常的安全堆栈，不将其标记为已处理。</summary>
    private void OnUiUnhandled(object sender,Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)=>_lifecycle?.Record("UiUnhandled",args.Exception,true);
    private void OnDomainUnhandled(object sender,System.UnhandledExceptionEventArgs args)=>_lifecycle?.Record(args.IsTerminating?"DomainTerminating":"DomainUnhandled",args.ExceptionObject as Exception,args.IsTerminating);
    private void OnTaskUnobserved(object? sender,UnobservedTaskExceptionEventArgs args)=>_lifecycle?.Record("TaskUnobserved",args.Exception);
    private IpcResponse HandleRequest(IpcRequest request,bool legacy)
    {
        var version=legacy?1:2;
        if(_exiting)return new(version,request.RequestId??"","stopping","程序正在退出，不再接收新任务。");
        if((legacy&&request.Version!=1)||!IpcProtocol.IsValid(request with{Version=legacy?2:request.Version}))return new(version,request.RequestId??"","invalidRequest","请求无效。");
        if(request.Operation=="ping")return new(version,request.RequestId,"ready","HybridMount.NET 已运行。");
        if(request.Operation=="showWindow") {_dispatcher?.TryEnqueue(RestoreWindow);return new(version,request.RequestId,"accepted","已请求显示窗口。");}
        if(legacy)return new(1,request.RequestId,"notImplemented","旧版菜单未执行文件操作，请更新扩展后重试。");
        try
        {
            if(request.Operation=="showSyncIssues")
            {
                var focus=Mounts.LocateUploadIssues(request.Paths);
                var queued=_dispatcher?.TryEnqueue(()=>{if(_exiting)return;RestoreWindow();TaskFocus.ShowIssues(focus.MountId,focus.Paths);})==true;
                return new(2,request.RequestId,queued?"accepted":"stopping",queued?"已定位同步问题。":"程序正在退出。");
            }
            if(request.Operation=="getPreview")
            {
                var png=Mounts.GetPreview(request.Paths[0],request.PreviewSize!.Value);
                return new(2,request.RequestId,png is null?"notFound":"ok","",PreviewPng:png,PreviewStatus:Mounts.PreviewState(request.Paths[0]));
            }
            if(request.Operation=="requestPreview")return new(2,request.RequestId,"accepted","",PreviewStatus:Mounts.RequestPreview(request.Paths[0],request.PreviewSize!.Value));
            if(request.Operation=="getOperation")
            {
                var job=Mounts.GetOperation(request.JobId!);
                return new(2,request.RequestId,job is null?"notFound":"ok","任务结果见任务页。",request.JobId,job is null?null:job with{Results=job.Results.Take(3).ToArray()});
            }
            if(request.Operation=="cancelOperation")return new(2,request.RequestId,Mounts.Cancel(request.JobId!)?"accepted":"notFound","已请求取消，已完成步骤保留。",request.JobId);
            var mount=Mounts.FindMount(request.Paths);var id=Mounts.Queue(mount,request.Operation,request.Paths);
            return new(2,request.RequestId,"accepted","操作已提交，可在任务页查看逐项结果。",id);
        }
        catch(Exception error){if(request.Operation is not("getPreview" or "requestPreview"))Logger.Queue("资源管理器","Warning",error.Message);return new(2,request.RequestId,"rejected",request.Operation is "getPreview" or "requestPreview"?"":error.Message);}
    }
    private void OnUploadIssueNotifications()=>_dispatcher?.TryEnqueue(()=>{if(!_exiting)MainWindow?.RefreshUploadIssueNotifications();});
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
        if(!_exiting&&Preferences.CloseToTray&&_tray?.IsRegistered==true)sender.Hide();else RequestExit();
    }
    /// <summary>原生托盘事件边界不允许异常穿出；重复点击等待同一次收尾。</summary>
    private async void RequestExit()
    {
        try{await ExitAsync();}
        catch(Exception error){_lifecycle?.Record("ExitBoundaryFailure",error,true);Environment.ExitCode=1;}
    }
    private Task ExitAsync()
    {
        lock(_exitSync)
        {
            if(_exitTask is not null)return _exitTask;
            _exiting=true;_lifecycle?.Record("ExitRequested",cancellationReason:"Shutdown");return _exitTask=ExitCoreAsync();
        }
    }
    private async Task ExitCoreAsync()
    {
        var stages=new ShutdownSequence((phase,error)=>
        {
            _lifecycle?.Record(phase,error,cancellationReason:"Shutdown");
            if(error is not null)Logger?.Queue("退出","Error",phase+"："+error.GetType().Name);
        });
        // 先向所有生产者发信号；一个服务的取消回调不能阻挡另一个服务收到停止通知。
        stages.Run("SignalIpc",()=>{_ = _ipc?.RequestStop();});
        stages.Run("SignalLegacyIpc",()=>{_ = _legacyIpc?.RequestStop();});
        stages.Run("SignalMounts",()=>Mounts?.RequestStop());
        stages.Run("SignalTaskFocus",()=>TaskFocus?.RequestStop());
        stages.Run("SignalNavigation",()=>Navigation.RequestStop());
        stages.Run("SignalPages",()=>MainWindow?.StopPages());
        await stages.RunAsync("IpcStop",()=>_ipc?.DisposeAsync().AsTask()??Task.CompletedTask);
        await stages.RunAsync("LegacyIpcStop",()=>_legacyIpc?.DisposeAsync().AsTask()??Task.CompletedTask);
        await stages.RunAsync("IssueNotificationsStop",async()=>{if(IssueNotifications is not null){IssueNotifications.Changed-=OnUploadIssueNotifications;await IssueNotifications.DisposeAsync();}});
        await stages.RunAsync("MountsStop",()=>Mounts?.DisposeAsync().AsTask()??Task.CompletedTask);
        stages.Run("UiSubscriptions",()=>{if(Appearance is not null)Appearance.Changed-=OnAppearanceChanged;if(Logger is not null)Logger.NotificationRequested-=OnNotification;});
        await stages.RunAsync("WindowResources",()=>MainWindow?.ReleaseResourcesAsync()??Task.CompletedTask);
        // 所有日志生产者已结束，再排空日志；后续阶段证据直接进入生命周期文件。
        await stages.RunAsync("LoggerStop",async()=>{if(Logger is not null){Logger.Queue("应用","Information","HybridMount.NET 正在退出。");await Logger.DisposeAsync();}});
        stages.Run("TrayRelease",()=>{if(_tray is not null){_tray.RestoreRequested-=RestoreWindow;_tray.ExitRequested-=RequestExit;_tray.Dispose();_tray=null;}});
        stages.Run("WindowRelease",()=>{_allowClose=true;MainWindow?.Close();MainWindow=null;});
        stages.Run("LegacyInstanceRelease",()=>{_legacyInstance?.Dispose();_legacyInstance=null;});
        stages.Run("InstanceRelease",()=>{_instance?.Dispose();_instance=null;});
        Environment.ExitCode=stages.Failures==0?0:1;
        _lifecycle?.Record(stages.Failures==0?"ExitCompleted":"ExitCompletedWithErrors",completed:stages.Failures==0,cancellationReason:"Shutdown");
        UnhandledException-=OnUiUnhandled;AppDomain.CurrentDomain.UnhandledException-=OnDomainUnhandled;TaskScheduler.UnobservedTaskException-=OnTaskUnobserved;
        try{Exit();}catch(Exception error){_lifecycle?.Record("ExitApplicationFailed",error,true);Environment.ExitCode=1;}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBox(nint owner,string text,string caption,uint type);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
}
