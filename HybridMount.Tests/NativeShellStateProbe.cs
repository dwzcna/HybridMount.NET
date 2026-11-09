using System.Text;
using System.Text.Json;
using HybridMount.Services.Mounting;
using Microsoft.Win32;
using global::Windows.Storage.Provider;

namespace HybridMount.Tests;

/// <summary>显式独立宿主的真实Shell对照，不属于普通Fact；所有根、日志、HTTP服务及注册表键均按本轮GUID隔离。</summary>
public static class NativeShellStateProbe
{
    /// <summary>维护子进程共用宿主映像，实际执行生产私有协议；普通模式不初始化WinUI、托盘或公共IPC。</summary>
    public static Task<int> MainAsync(string[] args)
    {
        if(args.Length>0&&args[0]=="--shell-maintenance")return Task.FromResult(ShellMaintenanceProcess.Run(args));
        if(args is ["--cleanup",var report])return CleanupOnlyAsync(report);
        return args.Length==2?RunAsync(args[0],args[1]):Task.FromResult(2);
    }
    /// <summary>只收尾本轮报告的隔离根；强制非打包、GUID路径及名称校验，不重连或访问远端。</summary>
    private static async Task<int> CleanupOnlyAsync(string report)
    {
        if(WinRegisterService.IsPackaged())throw new IOException("清理复核宿主必须非打包运行。");
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var mount=json.RootElement.GetProperty("Root").Deserialize<MountDefinition>()??throw new IOException("缺少测试根。");
        var area=Path.GetDirectoryName(Path.GetFullPath(report))!;
        if(!mount.Name.StartsWith("MySvr-00934-",StringComparison.Ordinal)||!mount.LocalPath.StartsWith(area+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            !Guid.TryParseExact(Path.GetFileName(mount.LocalPath).Replace("root-","",StringComparison.Ordinal),"N",out _))throw new IOException("不是本轮隔离根，拒绝清理。");
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(area,"cleanup-data"),registry);repository.SaveDefinition(mount with{RemovalPending=false});
        await using var logger=new LoggerService(Path.Combine(area,"cleanup.log.db"));
        await using var manager=new MountManager(repository,logger){RemovalShellForTests=new MaintenanceShellRegistration()};
        var job=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));
        await File.WriteAllTextAsync(report+".cleanup.json",JsonSerializer.Serialize(job,new JsonSerializerOptions{WriteIndented=true}));
        if(job.State!="Completed")return 1;
        Registry.CurrentUser.DeleteSubKeyTree(registry,false);return 0;
    }
    private static async Task<int> RunAsync(string output,string profile)
    {
        var report=new Dictionary<string,object?>();var area=Path.GetDirectoryName(output)!;UploadFlowTests.Fixture? test=null;
        var success=false;var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var packaged=WinRegisterService.IsPackaged();report["Profile"]=profile;report["Packaged"]=packaged;report["Package"]=packaged?global::Windows.ApplicationModel.Package.Current.Id.FullName:null;
            if((profile!="Unpackaged")!=packaged)throw new IOException("实际包身份与测试模式不符。");
            test=new UploadFlowTests.Fixture();test.Local=Path.Combine(area,"root-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(test.Local);test.Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
            test.Repository=new(Path.Combine(area,"data"),test.Registry);test.Logger=new(Path.Combine(area,"log.db"));test.Manager=new(test.Repository,test.Logger){RemovalShellForTests=new MaintenanceShellRegistration()};
            var names=new[]{"cloud.txt","cached.txt","pinned.txt","released.txt","edited.txt","failed.txt","uploading.txt"};foreach(var name in names)test.Dav.Put(name,Encoding.UTF8.GetBytes("native shell content "+name));
            var mount=await test.Manager.AddAsync("MySvr-00934-"+profile,test.Local,true,source:test.Dav.Options,credentials:new("user","app-password"),autoUpload:false);test.Id=mount.Id;
            await test.Manager.SetSchedulingForTestsAsync(test.Id,false);report["Root"]=mount;report["RootId"]=CloudRootRegistration.Id(mount);
            await File.WriteAllTextAsync(Path.Combine(area,"registered.json"),JsonSerializer.Serialize(new{mount.Id,mount.LocalPath,RootId=CloudRootRegistration.Id(mount),Profile=profile}));
            using(var key=Registry.LocalMachine.OpenSubKey(RecoveryShellRegistration.Manager+"\\"+CloudRootRegistration.Id(mount)))
            {report["AUMID"]=key?.GetValue("AUMID");report["NamespaceId"]=key?.GetValue("NamespaceCLSID");}
            foreach(var name in new[]{"cached.txt","released.txt"})await File.ReadAllBytesAsync(Path.Combine(test.Local,name));
            await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[Path.Combine(test.Local,"pinned.txt")]));
            await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[Path.Combine(test.Local,"released.txt")]));
            var original=test.Dav.Server.Handler;var holding=false;string? heldTarget=null;
            test.Dav.Server.Handler=async request=>
            {
                if(request.Target.Contains("failed.txt",StringComparison.Ordinal))return new HttpTestServer.Response(403,[]);
                if(holding&&request.Method=="PUT"&&request.Target.EndsWith("/uploading.txt",StringComparison.Ordinal)){heldTarget=request.Target;entered.TrySetResult();await release.Task;}
                return await original(request);
            };
            await File.WriteAllTextAsync(Path.Combine(test.Local,"failed.txt"),"intentional rejected upload");await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
            await Until(()=>test.Repository.Uploads(test.Id).Any(x=>x.LocalPath=="failed.txt"&&x.State is "Failed" or "Conflict"));
            await test.Manager.SetSchedulingForTestsAsync(test.Id,false);
            holding=true;await File.WriteAllTextAsync(Path.Combine(test.Local,"uploading.txt"),"intentional in-flight upload");await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await File.WriteAllTextAsync(Path.Combine(test.Local,"edited.txt"),"local edit remains uncommitted during read");
            Assert.Equal("Sending",test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="uploading.txt").State);report["HeldUploadTarget"]=heldTarget;
            var before=Facts(test,names);var requests=test.Dav.Server.Requests.Count;
            report["Physical"]=NativeShellInspector.Read(test.Local,test.Local,names);
            if(report["NamespaceId"] is string guid)report["Namespace"]=NativeShellInspector.Read("::"+guid,test.Local,names);
            report["FilesBefore"]=before;report["FilesAfter"]=Facts(test,names);report["RequestsBeforeRead"]=requests;report["RequestsAfterRead"]=test.Dav.Server.Requests.Count;
            report["Uploads"]=test.Repository.Uploads(test.Id).Select(x=>new{x.LocalPath,x.State,x.Error,x.Generation}).ToArray();report["Policy"]=CloudRootPolicy.Read(test.Local);
            report["DescriptorAllowPinning"]=(await CloudRootRegistration.CreateDescriptorAsync(mount)).AllowPinning;report["PreviewStatus"]=test.Manager.RequestPreview(Path.Combine(test.Local,"cloud.txt"),64);
            var physical=(NativeShellInspector.FolderFacts)report["Physical"]!;
            report["DefaultStateColumns"]=physical.Columns.Where(x=>(x.Flags&0x10)!=0).Select(x=>new{x.Name,x.Header,x.Flags}).ToArray();
            report["NativeMenuAvailable"]=physical.Files.Any(x=>x.Menu.Any(s=>s.Contains("始终保留",StringComparison.Ordinal)))&&physical.Files.Any(x=>x.Menu.Any(s=>s.Contains("释放空间",StringComparison.Ordinal)));
            Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal("disabled",report["PreviewStatus"]);
            Assert.Equal(0,physical.BindResult);Assert.Contains(physical.Columns,x=>x.Name=="System.StorageProviderUIStatus");
            Assert.True((bool)report["NativeMenuAvailable"]!);success=true;
        }
        catch(Exception error){report["Error"]=error.ToString();}
        finally
        {
            release.TrySetResult();
            if(test is not null)
            {
                try
                {
                    if(test.Id!=Guid.Empty&&test.Manager.Mounts.Any())
                    {await test.Manager.SetSchedulingForTestsAsync(test.Id,false);var job=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"clearAndRemove"));report["Removal"]=job;Assert.Equal("Completed",job.State);}
                    await test.Manager.DisposeAsync();await test.Logger.DisposeAsync();Registry.CurrentUser.DeleteSubKeyTree(test.Registry,false);await test.Dav.DisposeAsync();report["Cleanup"]=true;
                }
                catch(Exception error){report["Cleanup"]=false;report["CleanupError"]=error.ToString();success=false;try{await test.Manager.DisposeAsync();}catch{}}
            }
            report["Success"]=success;Directory.CreateDirectory(area);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        }
        return success?0:1;
    }
    private static object[] Facts(UploadFlowTests.Fixture test,string[] names)=>names.Select(name=>
    {
        var path=Path.Combine(test.Local,name);var info=CloudFilesNative.Inspect(path);return (object)new{Name=name,Identity=MountPathGuard.FileIdentity(path),Info=info?.Info,PlaceholderIdentity=info is null?null:Convert.ToHexString(info.Value.Identity),Shell=CloudShellState.Read(path),Attributes=(uint)File.GetAttributes(path),Hydrated=info is null||CloudFilesNative.HasFullContent(path,new FileInfo(path).Length)};
    }).ToArray();
    private static async Task Until(Func<bool> check)
    {var end=DateTime.UtcNow.AddSeconds(20);while(!check()){if(DateTime.UtcNow>end)throw new TimeoutException("隔离状态未就绪。");await Task.Delay(50);}}
}
