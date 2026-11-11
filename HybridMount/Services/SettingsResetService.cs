using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using global::Windows.Storage.Provider;

namespace HybridMount.Services;

/// <summary>当前模式的还原服务；只读预检与最终清理分离，不创建远端源、不注销挂载、不扫描其他模式数据。</summary>
internal static class SettingsResetService
{
    internal static SettingsResetScope CurrentScope()
    {
        var packaged=WinRegisterService.IsPackaged();
        var scope=SettingsResetScope.Create(packaged,Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),packaged?global::Windows.ApplicationModel.Package.Current.Id.FamilyName:null);
        var data=Path.GetDirectoryName(scope.MountDataDirectory)!;
        if(!string.Equals(Path.GetFullPath(ApplicationPaths.DataDirectory),data,StringComparison.OrdinalIgnoreCase))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_C8CADF5A6F81"));
        return scope;
    }
    /// <summary>只读核验失败直接传播；调用方持配置锁，目录检查可取消，不调用吞错的WinRT恢复枚举。</summary>
    internal static SettingsResetPlan Inspect(CancellationToken token)
    {
        var scope=CurrentScope();EnsureNoRegistrations(scope,token);
        var proofs=SettingsResetDirectories.Capture(scope,token);EnsureNoRecovery(scope,token);token.ThrowIfCancellationRequested();return new(scope,proofs);
    }
    internal static void Revalidate(SettingsResetPlan plan,CancellationToken token)
    {
        if(!plan.Scope.SameAs(CurrentScope()))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_E122A99B231B"));
        EnsureNoRegistrations(plan.Scope,token);SettingsResetDirectories.Validate(plan,token);EnsureNoRecovery(plan.Scope,token);token.ThrowIfCancellationRequested();
    }
    private static void EnsureNoRegistrations(SettingsResetScope scope,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 原始子键也算配置；残缺Definition不得因加载器忽略而被当成无挂载。
        using(var mounts=Registry.CurrentUser.OpenSubKey(scope.RegistryPath+@"\Mounts\"+scope.Mode))
            if(mounts?.SubKeyCount>0)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_0CFCBC69FEFE"));
        var prefix=scope.ProductScope+"!"+(WindowsIdentity.GetCurrent().User?.Value??throw new IOException("SID"))+"!";
        foreach(var item in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            token.ThrowIfCancellationRequested();
            if(item.Id.StartsWith(prefix,StringComparison.Ordinal))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_3CE4692D3A00"));
            var path=item.Path?.Path;if(string.IsNullOrEmpty(path))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_C8CADF5A6F81"));
            if(scope.Directories.Any(directory=>SettingsResetScope.Overlaps(directory,path)))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_A96A9F9A256A"));
        }
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
        {
            using var root=RegistryKey.OpenBaseKey(hive,view);using var manager=root.OpenSubKey(RecoveryShellRegistration.Manager);
            if(manager?.GetSubKeyNames().Any(name=>name.StartsWith(prefix,StringComparison.Ordinal))==true)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_3CE4692D3A00"));
            if(hive==RegistryHive.CurrentUser)
            {
                using var spaces=root.OpenSubKey(RecoveryShellRegistration.Namespace.TrimEnd(Path.DirectorySeparatorChar));
                if(spaces is not null)foreach(var name in spaces.GetSubKeyNames())
                {using var entry=spaces.OpenSubKey(name);if(entry?.GetValue("") is string id&&id.StartsWith(prefix,StringComparison.Ordinal))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_3CE4692D3A00"));}
            }
        }
        if(new RecoveryShellRegistration(false).List().Count>0)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_3CE4692D3A00"));
        // 包虚拟视图与真实视图都必须能查询；维护进程只读并在返回前退出。
        if(scope.Packaged&&(ShellMaintenanceProcess.Send("discover").Roots?.Length??0)>0)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_3CE4692D3A00"));
    }
    private static void EnsureNoRecovery(SettingsResetScope scope,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var recovery=Path.Combine(scope.MountDataDirectory,"Recovery");
        if(new MountRemovalCompletion(recovery,new RecoveryShellRegistration(false)).Journals().Any(j=>j.Stage!="Completed"||!j.ExternalVerified))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_4057AC6E04F7"));
        new OrphanedDirectoryRecovery(Path.Combine(recovery,"OrphanedDirectories"),()=>[]).EnsureNoPendingForSettingsReset();
        var renames=Path.Combine(scope.MountDataDirectory,"ExcludedRenames");
        if(Directory.Exists(renames))foreach(var file in Directory.EnumerateFiles(renames,"*.json"))
        {
            if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file),"N",out var id)||new ExcludedRenameJournal(scope.MountDataDirectory,id).Read().Any(item=>!item.Finished))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_4057AC6E04F7"));
        }
        foreach(var directory in new[]{recovery,renames})
            if(Directory.Exists(directory)&&Directory.EnumerateFiles(directory,"*.tmp",SearchOption.AllDirectories).Any())throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_4057AC6E04F7"));
    }
    /// <summary>仅在宿主、页面及日志写入者成功排空后调用；不重启宿主，不写任何会重建已删目录的日志。</summary>
    internal static async Task CompleteAsync(SettingsResetPlan plan)
    {
        await Task.Run(()=>Revalidate(plan,CancellationToken.None));
        await StartupService.ClearForSettingsResetAsync(plan.Scope.Packaged,plan.Scope.ProductScope);
        await Task.Run(()=>
        {
            using var registration=RootRegistryLease.Acquire();
            Revalidate(plan,CancellationToken.None);SqliteConnection.ClearAllPools();
            foreach(var directory in plan.Directories)
                try{SettingsResetDirectories.Delete(directory);}catch(Exception error){throw new IOException(directory.Path,error);}
            Registry.CurrentUser.DeleteSubKeyTree(plan.Scope.RegistryPath,false);
        });
    }
}
