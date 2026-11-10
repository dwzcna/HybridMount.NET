using System.Text.Json;
using global::Windows.Security.Cryptography;
using global::Windows.Storage.Provider;
namespace HybridMount.Tests;

/// <summary>显式隔离MSIX探针，以真实包身份验证旧主映像图标刷新；不是非打包xUnit宿主中的假通过。</summary>
public static class NativeRootIconProbe
{
    private static async Task<StorageProviderSyncRootInfo> FindAsync(MountDefinition mount)
    {
        var until=DateTime.UtcNow.AddSeconds(5);
        do
        {
            StorageProviderSyncRootInfo value;
            try{value=StorageProviderSyncRootManager.GetSyncRootInformationForId(CloudRootRegistration.Id(mount));}
            catch(System.Runtime.InteropServices.COMException error)when(error.HResult==unchecked((int)0x80070490))
            {value=StorageProviderSyncRootManager.GetSyncRootInformationForFolder(await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(mount.LocalPath));}
            if(value is not null){Assert.Equal(CloudRootRegistration.Id(mount),value.Id);return value;}await Task.Delay(50);
        }while(DateTime.UtcNow<until);
        throw new IOException("真实包身份未能读取本轮同步根描述符。");
    }
    /// <summary>图标之外的描述符逐字段记录；只读取既有元数据，不下载内容。</summary>
    private static string Facts(StorageProviderSyncRootInfo info)=>JsonSerializer.Serialize(new
    {
        info.Id,Path=info.Path.Path,info.DisplayNameResource,info.HydrationPolicy,info.HydrationPolicyModifier,info.PopulationPolicy,
        info.InSyncPolicy,info.HardlinkPolicy,info.AllowPinning,info.ShowSiblingsAsGroup,info.Version,info.ProviderId,info.ProtectionMode,info.RecycleBinUri,
        Context=CryptographicBuffer.EncodeToHexString(info.Context),Properties=info.StorageProviderItemPropertyDefinitions.Select(x=>new{x.Id,x.DisplayNameResource}).ToArray()
    });
    /// <summary>仅操作新GUID根和回环源；失败保留实际结果，finally等待夹具收尾，不访问正式挂载。</summary>
    public static async Task<int> RunAsync(string area)
    {
        var report=new Dictionary<string,object?>();UploadFlowTests.Fixture? test=null;var passed=false;
        try
        {
            Assert.True(WinRegisterService.IsPackaged());report["Package"]=global::Windows.ApplicationModel.Package.Current.Id.FullName;
            test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var mount=test.Manager.Mounts.Single().Definition;var path=Path.Combine(test.Local,"文件.bin");var before=CloudFilesNative.InspectPersisted(path);
            report["Root"]=mount;report["RootId"]=CloudRootRegistration.Id(mount);
            using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\"+CloudRootRegistration.Id(mount))){report["Namespace"]=key?.GetValue("NamespaceCLSID");report["AUMID"]=key?.GetValue("AUMID");}
            var rootIdentity=CloudFilesNative.RootIdentity(test.Local,true);var fileId=MountPathGuard.FileIdentity(path);var rootFileId=MountPathGuard.FileIdentity(test.Local);
            await test.Manager.DisconnectAsync(test.Id);
            var info=await FindAsync(mount);var oldIcon=Path.Combine(AppContext.BaseDirectory,"HybridMount.NET.exe")+",0";info.IconResource=oldIcon;StorageProviderSyncRootManager.Register(info);var facts=Facts(await FindAsync(mount));var requests=test.Dav.Server.Requests.Count;report["BeforeDescriptor"]=facts;report["BeforeIcon"]=oldIcon;
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>CloudRootRegistration.EnsureAsync(mount,cancel.Token));Assert.Equal(oldIcon,(await FindAsync(mount)).IconResource);
            }
            await CloudRootRegistration.EnsureAsync(mount,default);var expected=Path.Combine(AppContext.BaseDirectory,"HybridMount.exe")+",0";var updated=await FindAsync(mount);
            Assert.Equal(expected,updated.IconResource);Assert.Equal(facts,Facts(updated));
            await CloudRootRegistration.EnsureAsync(mount,default);updated=await FindAsync(mount);Assert.Equal(expected,updated.IconResource);Assert.Equal(facts,Facts(updated));report["AfterDescriptor"]=Facts(updated);report["AfterIcon"]=updated.IconResource;
            var after=CloudFilesNative.InspectPersisted(path);Assert.Equal(rootIdentity,CloudFilesNative.RootIdentity(test.Local,true));Assert.Equal(fileId,MountPathGuard.FileIdentity(path));Assert.Equal(rootFileId,MountPathGuard.FileIdentity(test.Local));
            Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);Assert.Equal(before.Info.ModifiedSize,after.Info.ModifiedSize);Assert.Equal(requests,test.Dav.Server.Requests.Count);
            report["RequestsBefore"]=requests;report["RequestsAfter"]=test.Dav.Server.Requests.Count;report["IdentityAndContentStateUnchanged"]=true;passed=true;
        }
        catch(Exception error){report["Error"]=error.ToString();}
        finally
        {
            try{if(test is not null)await test.DisposeAsync();report["Cleanup"]=true;}catch(Exception error){passed=false;report["Cleanup"]=false;report["CleanupError"]=error.ToString();}
            report["Passed"]=passed;Directory.CreateDirectory(area);await File.WriteAllTextAsync(Path.Combine(area,"icon-proof.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            await NativeRuntimeClient.StopSharedAsync();
        }
        return passed?0:1;
    }
}
