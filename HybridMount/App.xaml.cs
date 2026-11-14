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
    private SettingsResetPlan? _settingsReset;
    public bool IsExiting=>_exiting;
    private LifecycleDiagnostics? _lifecycle;
    public new static App Current=>(App)Application.Current;
    public static string Version=>System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion.Split('+')[0]??typeof(App).Assembly.GetName().Version?.ToString()??global::HybridMount.Services.Localization.UiText.Text("Text_F1E7BEC171AE");
    public NavigationService Navigation{get;}=new();
    public MainWindow? MainWindow{get;private set;}
    public LoggerService Logger{get;private set;}=null!;
    public AppearanceSettingsService Appearance{get;private set;}=null!;
    public ApplicationPreferencesService Preferences{get;private set;}=null!;
    public CloudOperationSettings CloudOperations{get;private set;}=null!;
    public IStartupService Startup{get;private set;}=null!;
    public IUiDispatcher UiDispatcher{get;private set;}=null!;
    public TaskFocusService TaskFocus{get;private set;}=null!;
    public UploadIssueNotificationService IssueNotifications{get;private set;}=null!;
    public TasksQueueService TasksQueue{get;private set;}=null!;
    public MountManager Mounts{get;private set;}=null!;
    internal MountIndexFormat IndexFormat {get;private set;}=new("New");
    internal bool RuntimeAvailable=>Mounts is not null;
    internal string IndexRestrictionMessage=>Services.Localization.UiText.Text(IndexFormat.State switch
    {"Old"=>"IndexOldResetRequired","Newer"=>"IndexNewerVersion","Corrupt"=>"IndexCorrupt","AccessDenied"=>"IndexAccessDenied",_=>"IndexReadFailed"});
    public Services.Localization.UiLanguageService Languages{get;private set;}=null!;
    private IDisposable? _languageScope;private Services.Localization.NativeUiLanguageBridge? _languageBridge;
    public App():this(null){}
    protected internal App(Services.Localization.UiLanguageService? languages)
    {
        if(languages is null)
        {
            try{_languageBridge=new();}catch(Exception error){System.Diagnostics.Trace.TraceWarning("Language bridge initialization: 0x{0:X8}",error.HResult);}
            var queue=DispatcherQueue.GetForCurrentThread();
            languages=new(new Services.Localization.RegistryUiLanguageStore(ApplicationPaths.RegistryPath),new Services.Localization.MrtUiTextProvider(),
                dispatch:action=>{if(queue is null||queue.HasThreadAccess)action();else queue.TryEnqueue(()=>action());},
                publish:value=>Services.Localization.UiLanguagePublication.Publish(value,WinRegisterService.IsPackaged,
                    language=>global::Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride=language,
                    selection=>_languageBridge?.Publish(selection)));
        }
        Languages=languages;_languageScope=Services.Localization.UiText.Use(languages);try{InitializeComponent();}catch{_languageScope.Dispose();_languageBridge?.Dispose();throw;}
    }
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
                catch{try{await IpcClient.SendAsync(request with{Version=1},IpcProtocol.LegacyPipeName);}catch(Exception error){if(!silent)MessageBox(0,global::HybridMount.Services.Localization.UiText.Text("Text_2F46A49FD27E",error.Message),"HybridMount.NET",0x10);}}
                _legacyInstance.Dispose();_instance.Dispose();_legacyInstance=null;_instance=null;Exit();return;
            }
            _lifecycle=new LifecycleDiagnostics(ApplicationPaths.LogsDirectory,Version,WinRegisterService.IsPackaged()?"Packaged":"Unpackaged");
            UnhandledException+=OnUiUnhandled;AppDomain.CurrentDomain.UnhandledException+=OnDomainUnhandled;TaskScheduler.UnobservedTaskException+=OnTaskUnobserved;
            Logger=await Task.Run(()=>new LoggerService());
            if(_lifecycle.PreviousSessionIncomplete)Logger.Queue("生命周期","Warning","上次会话缺少退出完成记录，原因待定位；请查看 Logs 中的 lifecycle 诊断。该记录不单独证明发生崩溃。");
            Appearance=new AppearanceSettingsService();if(Appearance.ErrorStatus is not null)Logger.Queue("外观","Warning","外观偏好读取失败，已采用安全回退设置。");
            Preferences=new ApplicationPreferencesService(Logger);Startup=new StartupService(Logger);
            CloudOperations=await Task.Run(()=>new CloudOperationSettings(Logger));
            // 只读格式预检先于仓库和全部挂载业务服务；不迁移旧索引或因关闭提示解除限制。
            IndexFormat=await Task.Run(()=>MountIndexFormat.Inspect(Path.Combine(ApplicationPaths.DataDirectory,"Mounting")));
            if(IndexFormat.CanStart)
            {
                TasksQueue=new(CloudOperations.Current);CloudOperations.Bind(TasksQueue);
                Mounts=await Task.Run(()=>new MountManager(new MountRepository(Path.Combine(ApplicationPaths.DataDirectory,"Mounting")),Logger,tasksQueue:TasksQueue){StopObserver=RecordMountStop});
                IssueNotifications=new(Mounts);
            }
            else
            {
                Navigation.PageAllowed=page=>page is ApplicationPage.Settings or ApplicationPage.Logs;
                Logger.Queue("应用数据","Warning",IndexRestrictionMessage);
            }
            TaskFocus=new(Navigation);
            Appearance.Changed+=OnAppearanceChanged;Logger.NotificationRequested+=OnNotification;
            try{_tray=new TrayIconService();_tray.RestoreRequested+=RestoreWindow;_tray.ExitRequested+=RequestExit;}
            catch(Exception error){Logger.QueueException("托盘","Error","操作未完成",error);}
            _ipc=new IpcServerService(Logger){Handler=request=>HandleRequest(request,false)};_ipc.Start();
            _legacyIpc=new IpcServerService(Logger,IpcProtocol.LegacyPipeName){Handler=request=>HandleRequest(request,true)};_legacyIpc.Start();
            if(RuntimeAvailable)_=Mounts.StartAsync();
            Logger.Queue("应用","Information",$"HybridMount.NET {Version} 已启动。");
            if(!silent||_tray?.IsRegistered!=true||!RuntimeAvailable)RestoreWindow();
            _lifecycle.Record("Ready");
        }
        catch(Exception error){Services.HostDiagnosticStore.Record("Startup",error);Logger?.QueueException("启动","Error","应用启动失败",error);_lifecycle?.Record("StartupFailed",error,true);MessageBox(0,global::HybridMount.Services.Localization.UiText.Text("Text_77C4A50009F0",Models.Diagnostics.SafeDiagnostic.ExceptionSummary(error),error.Message),"HybridMount.NET",0x10);await ExitAsync();}
    }
    private void RecordMountStop(string phase,Exception? error)
    {
        var reason=phase.StartsWith("ConnectionCanceled.",StringComparison.Ordinal)?phase["ConnectionCanceled.".Length..]:_exiting?"Shutdown":null;
        _lifecycle?.Record("Mounts."+phase,error,cancellationReason:reason);
    }
    /// <summary>只记录未处理异常的安全堆栈，不将其标记为已处理。</summary>
    private void OnUiUnhandled(object sender,Microsoft.UI.Xaml.UnhandledExceptionEventArgs args){_lifecycle?.Record("UiUnhandled",args.Exception,true);Services.HostDiagnosticStore.Record("Startup",args.Exception);Logger?.QueueException("应用异常","Error","UI未处理异常",args.Exception);}
    private void OnDomainUnhandled(object sender,System.UnhandledExceptionEventArgs args){_lifecycle?.Record(args.IsTerminating?"DomainTerminating":"DomainUnhandled",args.ExceptionObject as Exception,args.IsTerminating);if(args.ExceptionObject is Exception error){Services.HostDiagnosticStore.Record("Startup",error);Logger?.QueueException("应用异常","Error","域未处理异常",error);}}
    private void OnTaskUnobserved(object? sender,UnobservedTaskExceptionEventArgs args){_lifecycle?.Record("TaskUnobserved",args.Exception);Services.HostDiagnosticStore.Record("Startup",args.Exception);Logger?.QueueException("应用异常","Warning","未观察任务异常",args.Exception);}
    private IpcResponse HandleRequest(IpcRequest request,bool legacy)
    {
        var version=legacy?1:2;
        if(_exiting)return new(version,request.RequestId??"","stopping",global::HybridMount.Services.Localization.UiText.Text("Text_E6A7881C23A2"));
        if((legacy&&request.Version!=1)||!IpcProtocol.IsValid(request with{Version=legacy?2:request.Version}))return new(version,request.RequestId??"","invalidRequest",global::HybridMount.Services.Localization.UiText.Text("Text_37CE123BBAD6"));
        if(request.Operation=="ping")return new(version,request.RequestId,"ready",global::HybridMount.Services.Localization.UiText.Text("Text_8D3061703E17"));
        if(request.Operation=="showWindow") {_dispatcher?.TryEnqueue(RestoreWindow);return new(version,request.RequestId,"accepted",global::HybridMount.Services.Localization.UiText.Text("Text_F71FF9567A7C"));}
        if(!RuntimeAvailable)return new(version,request.RequestId,"rejected",IndexRestrictionMessage);
        if(legacy)return new(1,request.RequestId,"notImplemented",global::HybridMount.Services.Localization.UiText.Text("Text_A028D7449DF0"));
        try
        {
            if(request.Operation=="showSyncIssues")
            {
                var focus=Mounts.LocateUploadIssues(request.Paths);
                var queued=_dispatcher?.TryEnqueue(()=>{if(_exiting)return;RestoreWindow();TaskFocus.ShowIssues(focus.MountId,focus.Paths);})==true;
                return new(2,request.RequestId,queued?"accepted":"stopping",queued?global::HybridMount.Services.Localization.UiText.Text("Text_A5D176FA10BC"):global::HybridMount.Services.Localization.UiText.Text("Text_06F0E237D44A"));
            }
            if(request.Operation=="getPreview")
            {
                var png=Mounts.GetPreview(request.Paths[0],request.PreviewSize!.Value);
                return new(2,request.RequestId,!ShellExtensionPolicy.CustomThumbnailsEnabled?"unsupported":png is null?"notFound":"ok","",PreviewPng:png,PreviewStatus:Mounts.PreviewState(request.Paths[0]));
            }
            if(request.Operation=="requestThumbnail")return new(2,request.RequestId,ShellExtensionPolicy.CustomThumbnailsEnabled?"accepted":"unsupported","",PreviewStatus:Mounts.RequestThumbnail(request.Paths[0],request.PreviewSize!.Value));
            if(request.Operation=="requestLocalPreview")return new(2,request.RequestId,ShellExtensionPolicy.CustomThumbnailsEnabled?"accepted":"unsupported","",PreviewStatus:Mounts.RequestLocalPreview(request.Paths[0],request.PreviewSize!.Value));
            if(request.Operation=="requestPreview")return new(2,request.RequestId,ShellExtensionPolicy.CustomThumbnailsEnabled?"accepted":"unsupported","",PreviewStatus:Mounts.RequestPreview(request.Paths[0],request.PreviewSize!.Value));
            if(request.Operation=="getOperation")
            {
                var job=Mounts.GetOperation(request.JobId!);
                return new(2,request.RequestId,job is null?"notFound":"ok",global::HybridMount.Services.Localization.UiText.Text("Text_65C8EFD51BD1"),request.JobId,job is null?null:job.WithoutLocalEvidence() with{Results=job.Results.Take(3).ToArray()});
            }
            if(request.Operation=="cancelOperation")return new(2,request.RequestId,Mounts.Cancel(request.JobId!)?"accepted":"notFound",global::HybridMount.Services.Localization.UiText.Text("Text_234B77A1E2E0"),request.JobId);
            var mount=Mounts.FindMount(request.Paths);var id=Mounts.Queue(mount,request.Operation,request.Paths);
            return new(2,request.RequestId,"accepted",global::HybridMount.Services.Localization.UiText.Text("Text_2E89004EB19C"),id);
        }
        catch(Exception error){if(request.Operation is not("getPreview" or "requestPreview" or "requestLocalPreview" or "requestThumbnail"))Logger.QueueException("资源管理器","Warning","操作未完成",error);return new(2,request.RequestId,"rejected",request.Operation is "getPreview" or "requestPreview" or "requestLocalPreview" or "requestThumbnail"?"":error.Message);}
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
        if(!_exiting&&Preferences.CloseToTray&&_tray?.IsRegistered==true)sender.Hide();else RequestExit();
    }
    /// <summary>原生托盘事件边界不允许异常穿出；重复点击等待同一次收尾。</summary>
    private async void RequestExit()
    {
        try{await ExitAsync();}
        catch(Exception error){_lifecycle?.Record("ExitBoundaryFailure",error,true);Logger?.QueueException("退出","Error","退出边界失败",error);Environment.ExitCode=1;}
    }
    /// <summary>页面只请求应用生命周期动作，挂载检查仍由应用协调器持锁执行。</summary>
    private readonly ExplorerEntryMaintenance _explorerMaintenance=new();
    internal Task<IReadOnlyList<ExplorerEntryCandidate>> DiscoverExplorerEntriesAsync(CancellationToken token) =>
        RuntimeAvailable?Mounts.DiscoverExplorerEntriesAsync(token):_explorerMaintenance.DiscoverAsync(token);
    internal Task<IReadOnlyList<ExplorerEntryResult>> RemoveExplorerEntriesAsync(IReadOnlyList<ExplorerEntryCandidate> entries,
        CancellationToken token,Action<int,int,string> progress) => RuntimeAvailable?
        Mounts.RemoveExplorerEntriesAsync(entries,token,progress):_explorerMaintenance.RemoveAsync(entries,token,progress);
    internal async Task<SettingsResetInspection> InspectSettingsResetAsync(CancellationToken token)
    {
        try
        {
            var result=RuntimeAvailable?await Mounts.InspectSettingsResetAsync(token):await _explorerMaintenance.RunAsync(SettingsResetService.Inspect,token);
            return !RuntimeAvailable&&IndexFormat.State=="Old"&&result.BlockedReason is {} reason?
                result with{BlockedReason=Services.Localization.UiText.Text("IndexResetBlocked",reason)}:result;
        }
        catch(IOException error)when(!RuntimeAvailable&&IndexFormat.State=="Old")
        {throw new IOException(Services.Localization.UiText.Text("IndexResetBlocked",error.Message),error);}
    }
    /// <summary>已确认的当前模式还原先在配置锁内复核并停止接纳业务；删除仅在完整退出阶段执行。</summary>
    internal async Task ResetSettingsAndExitAsync(SettingsResetPlan plan,CancellationToken token)
    {
        if(_exiting)throw new OperationCanceledException(token);
        if(RuntimeAvailable)await Mounts.BeginSettingsResetAsync(plan,token);
        else await _explorerMaintenance.RunAsync(cancel=>{SettingsResetService.Revalidate(plan,cancel);return true;},token);
        lock(_exitSync){if(_exitTask is not null)throw new OperationCanceledException(token);_settingsReset=plan;}
        await ExitAsync();
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
        var resetStopFailures=new List<string>();
        var stages=new ShutdownSequence((phase,error)=>
        {
            _lifecycle?.Record(phase,error,cancellationReason:"Shutdown");
            if(error is not null){resetStopFailures.Add(phase+" 0x"+error.HResult.ToString("X8"));Logger?.Queue("退出","Error",phase+"："+error.GetType().Name);}
        });
        // 先向所有生产者发信号；一个服务的取消回调不能阻挡另一个服务收到停止通知。
        stages.Run("SignalIpc",()=>{_ = _ipc?.RequestStop();});
        stages.Run("SignalLegacyIpc",()=>{_ = _legacyIpc?.RequestStop();});
        stages.Run("SignalTaskQueue",()=>TasksQueue?.RequestStop());
        stages.Run("SignalExplorerMaintenance",_explorerMaintenance.RequestStop);
        stages.Run("SignalMounts",()=>Mounts?.RequestStop());
        stages.Run("SignalTaskFocus",()=>TaskFocus?.RequestStop());
        stages.Run("SignalNavigation",()=>Navigation.RequestStop());
        stages.Run("SignalPages",()=>MainWindow?.StopPages());
        await stages.RunAsync("IpcStop",()=>_ipc?.DisposeAsync().AsTask()??Task.CompletedTask);
        await stages.RunAsync("LegacyIpcStop",()=>_legacyIpc?.DisposeAsync().AsTask()??Task.CompletedTask);
        await stages.RunAsync("IssueNotificationsStop",async()=>{if(IssueNotifications is not null){await IssueNotifications.DisposeAsync();}});
        await stages.RunAsync("ExplorerMaintenanceStop",()=>_explorerMaintenance.DisposeAsync().AsTask());
        await stages.RunAsync("MountsStop",()=>Mounts?.DisposeAsync().AsTask()??Task.CompletedTask);
        await stages.RunAsync("TaskQueueStop",()=>TasksQueue?.DisposeAsync().AsTask()??Task.CompletedTask);
        await stages.RunAsync("NativeRuntimeStop",()=>NativeRuntimeClient.StopSharedAsync());
        stages.Run("UiSubscriptions",()=>{if(Appearance is not null)Appearance.Changed-=OnAppearanceChanged;if(Logger is not null)Logger.NotificationRequested-=OnNotification;});
        await stages.RunAsync("WindowResources",()=>MainWindow?.ReleaseResourcesAsync()??Task.CompletedTask);
        // 所有日志生产者已结束，再排空日志；后续阶段证据直接进入生命周期文件。
        await stages.RunAsync("LoggerStop",async()=>{if(Logger is not null){Logger.Queue("应用","Information","HybridMount.NET 正在退出。");await Logger.DisposeAsync();}});
        stages.Run("TrayRelease",()=>{if(_tray is not null){_tray.RestoreRequested-=RestoreWindow;_tray.ExitRequested-=RequestExit;_tray.Dispose();_tray=null;}});
        if(_settingsReset is null)stages.Run("WindowRelease",()=>{_allowClose=true;MainWindow?.Close();MainWindow=null;});
        stages.Run("LanguageRelease",()=>{if(_settingsReset is null){_languageScope?.Dispose();_languageScope=null;}Languages.Dispose();_languageBridge?.Dispose();_languageBridge=null;});
        if(_settingsReset is null)
        {
            stages.Run("LegacyInstanceRelease",()=>{_legacyInstance?.Dispose();_legacyInstance=null;});
            stages.Run("InstanceRelease",()=>{_instance?.Dispose();_instance=null;});
        }
        Environment.ExitCode=stages.Failures==0?0:1;
        _lifecycle?.Record(stages.Failures==0?"ExitCompleted":"ExitCompletedWithErrors",completed:stages.Failures==0,cancellationReason:"Shutdown");
        UnhandledException-=OnUiUnhandled;AppDomain.CurrentDomain.UnhandledException-=OnDomainUnhandled;TaskScheduler.UnobservedTaskException-=OnTaskUnobserved;
        if(_settingsReset is {} reset)
        {
            // 最后记录已经刷盘；随后彻底停用生命周期写入，不能在删除后重新创建Logs。
            _lifecycle=null;
            try
            {
                if(stages.Failures!=0)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_542813A4B00F")+"\n"+string.Join("\n",resetStopFailures));
                await SettingsResetService.CompleteAsync(reset);
            }
            catch(Exception error)
            {
                Environment.ExitCode=1;
                if(MainWindow is {} window)try{await window.ShowSettingsResetFailureAsync(error);}catch(Exception displayError){System.Diagnostics.Debug.WriteLine(displayError.GetType().Name);}
            }
            stages.Run("LanguageScopeRelease",()=>{_languageScope?.Dispose();_languageScope=null;});
            stages.Run("WindowRelease",()=>{_allowClose=true;MainWindow?.Close();MainWindow=null;});
        }
        if(_settingsReset is not null)
        {
            stages.Run("LegacyInstanceRelease",()=>{_legacyInstance?.Dispose();_legacyInstance=null;});
            stages.Run("InstanceRelease",()=>{_instance?.Dispose();_instance=null;});
        }
        if(stages.Failures>0)Environment.ExitCode=1;
        try{Exit();}catch(Exception error){_lifecycle?.Record("ExitApplicationFailed",error,true);Environment.ExitCode=1;}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBox(nint owner,string text,string caption,uint type);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
}
