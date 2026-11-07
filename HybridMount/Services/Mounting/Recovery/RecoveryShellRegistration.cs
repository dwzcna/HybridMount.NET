using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Globalization;
using HybridMount.Models.Mounting;
using Microsoft.Win32;
using Windows.Storage.Provider;

namespace HybridMount.Services.Mounting;

internal sealed record RecoveryRegistration(string Id,string Path,string? NamespaceId=null,bool ShellOnly=false);
internal sealed record RecoveryRegistryValue(string Name,RegistryValueKind Kind,string[] Data);
internal sealed record RecoveryRegistryTree(RecoveryRegistryValue[] Values,Dictionary<string,RecoveryRegistryTree> Children);
internal sealed record RecoveryRegistryBackup(RegistryHive Hive,string Path,string Owner,RecoveryRegistryTree Tree);
internal sealed record RecoveryHiddenValue(string Path,string Owner,RecoveryRegistryValue Value);
internal sealed record RecoveryShellProof(RecoveryRegistryBackup[] Roots,RecoveryRegistryBackup[] Namespace,string? NamespaceId,
    RecoveryHiddenValue[]? Hidden=null,RecoveryShellProof? External=null);
/// <summary>隔离系统注册访问边界；核验返回精确残项，旧测试适配器仍可保持原调用。</summary>
internal interface IRecoveryShellRegistration
{
    IReadOnlyList<RecoveryRegistration> List();
    RecoveryShellProof? Capture(OrphanedMountCandidate root);
    void Unregister(string id);
    void Cleanup(OrphanedMountCandidate root,RecoveryShellProof? proof,Action verifyContent);
    string[] Verify(OrphanedMountCandidate root,RecoveryShellProof? proof)=>[];
}
/// <summary>只处理当前用户、本产品的精确每根注册。包内视图与真实视图分别执行相同快照校验，不接受任意注册路径。</summary>
internal sealed class RecoveryShellRegistration(bool includeWinRt=true):IRecoveryShellRegistration
{
    internal const string Manager=@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
    internal const string Namespace=@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\";
    internal const string Classes=@"Software\Classes\CLSID\";
    internal static readonly string[] HiddenPaths=[@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu"];
    private readonly string _sid=WindowsIdentity.GetCurrent().User!.Value;
    private static readonly RegistryHive[] Hives=[RegistryHive.LocalMachine,RegistryHive.CurrentUser];
    internal static IRecoveryShellRegistration Create()
    {
#if HYBRIDMOUNT_HEADLESS_TEST
        return new RecoveryShellRegistration();
#else
        return new MaintenanceShellRegistration();
#endif
    }
    public IReadOnlyList<RecoveryRegistration> List()
    {
        var found=new List<RecoveryRegistration>();
        try{if(includeWinRt)foreach(var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())if(OrphanedMountRecovery.TryParseId(root.Id,_sid,out _)&&root.Path?.Path is {} path)found.Add(new(root.Id,path));}
        catch(Exception error)when(error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException){}
        foreach(var hive in Hives)
        {
            using var baseKey=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);using var manager=baseKey.OpenSubKey(Manager);if(manager is null)continue;
            foreach(var name in manager.GetSubKeyNames())
            {
                if(!OrphanedMountRecovery.TryParseId(name,_sid,out _))continue;
                using var root=manager.OpenSubKey(name);using var users=root?.OpenSubKey("UserSyncRoots");
                if(users?.GetValue(_sid,null,RegistryValueOptions.DoNotExpandEnvironmentNames) is string path)found.Add(new(name,path,root?.GetValue("NamespaceCLSID") as string));
            }
        }
        // SyncRootManager可能已经删除；精确配对的Namespace默认值与CLSID目标仍能证明入口归属。
        using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var spaces=user.OpenSubKey(Namespace.TrimEnd(Path.DirectorySeparatorChar));
        if(spaces is not null)foreach(var name in spaces.GetSubKeyNames())
        {
            if(!Guid.TryParseExact(name,"B",out var guid)||guid==Guid.Empty)continue;
            using var space=spaces.OpenSubKey(name);if(space?.GetValue("") is not string id||!OrphanedMountRecovery.TryParseId(id,_sid,out _))continue;
            using var bag=user.OpenSubKey(Classes+name+@"\Instance\InitPropertyBag");
            if(bag?.GetValue("TargetFolderPath",null,RegistryValueOptions.DoNotExpandEnvironmentNames) is string path&&Path.IsPathFullyQualified(path)&&!path.StartsWith(@"\\",StringComparison.Ordinal))
                found.Add(new(id,Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),guid.ToString("B").ToUpperInvariant(),true));
        }
        return found.DistinctBy(x=>(x.Id,x.Path,x.NamespaceId)).ToArray();
    }
    private void ValidateRoot(OrphanedMountCandidate root)
    {
        if(!OrphanedMountRecovery.TryParseId(root.SyncRootId,_sid,out var id)||id!=root.MountId)throw new IOException("Shell恢复ID不属于当前用户。");
        if(!Path.IsPathFullyQualified(root.LocalPath)||root.LocalPath.StartsWith(@"\\",StringComparison.Ordinal)||!string.Equals(root.LocalPath,Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.LocalPath)),StringComparison.OrdinalIgnoreCase)||
            SamePath(root.LocalPath,Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root.LocalPath)!)))throw new IOException("Shell目标不是本地非卷根路径。");
    }
    public RecoveryShellProof Capture(OrphanedMountCandidate root)
    {
        ValidateRoot(root);var roots=new List<RecoveryRegistryBackup>();var namespaceId=NormalizeGuid(root.NamespaceId);
        foreach(var hive in Hives)
        {
            using var baseKey=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);using var key=baseKey.OpenSubKey(Manager+"\\"+root.SyncRootId);if(key is null)continue;
            AssertRoot(key,root);roots.Add(Backup(hive,Manager+"\\"+root.SyncRootId,key));
            if(key.GetValue("NamespaceCLSID") is string raw)
            {var current=NormalizeGuid(raw);if(namespaceId is not null&&namespaceId!=current)throw new IOException("多个注册的命名空间不一致。");namespaceId=current;}
        }
        if(namespaceId is null)
        {
            var hints=NamespaceIds(root.SyncRootId).ToArray();
            if(hints.Length>1)throw new IOException("多个Shell入口指向同一根，需逐项核对。");namespaceId=hints.SingleOrDefault();
        }
        var names=new List<RecoveryRegistryBackup>();var hidden=new List<RecoveryHiddenValue>();
        if(namespaceId is not null)
        {
            AssertUnshared(namespaceId,root.SyncRootId);using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
            using var space=user.OpenSubKey(Namespace+namespaceId);using var cls=user.OpenSubKey(Classes+namespaceId);
            if(space is not null){AssertNamespace(space,root);names.Add(Backup(RegistryHive.CurrentUser,Namespace+namespaceId,space));}
            if(cls is not null)
            {
                AssertClass(cls,root);if(space is null&&roots.Count==0)throw new IOException("缺少可证明归属的配对Shell入口，保留类注册。");
                names.Add(Backup(RegistryHive.CurrentUser,Classes+namespaceId,cls));
            }
            foreach(var path in HiddenPaths)
            {using var key=user.OpenSubKey(path);if(key is not null&&key.GetValueNames().Contains(namespaceId,StringComparer.OrdinalIgnoreCase))hidden.Add(new(path,Owner(key),ReadValue(key,namespaceId)));}
        }
        if(names.Any(x=>x.Owner!=_sid)||hidden.Any(x=>x.Owner!=_sid))throw new IOException("每根Shell注册或隐藏值的所有者不是当前用户。");
        return new(roots.ToArray(),names.ToArray(),namespaceId,hidden.ToArray());
    }
    public void Unregister(string id)=>StorageProviderSyncRootManager.Unregister(id);
    public void Cleanup(OrphanedMountCandidate root,RecoveryShellProof? proof,Action verifyContent)
    {
        ValidateRoot(root);ValidateProof(root,proof);verifyContent();
        // 先核验所有尚存快照，再开始删除；部分删除后的重试允许已不存在，绝不重新捕获变化的值。
        foreach(var item in (proof?.Roots??[]).Concat(proof?.Namespace??[]))
        {using var baseKey=RegistryKey.OpenBaseKey(item.Hive,RegistryView.Registry64);using var key=baseKey.OpenSubKey(item.Path);if(key is not null)AssertUnchanged(item,key);}
        foreach(var value in proof?.Hidden??[])AssertHidden(value);
        if(proof?.NamespaceId is {} guid)AssertUnshared(guid,root.SyncRootId);
        foreach(var backup in proof?.Roots??[])
        {
            verifyContent();using var baseKey=RegistryKey.OpenBaseKey(backup.Hive,RegistryView.Registry64);
            using(var key=baseKey.OpenSubKey(backup.Path)){if(key is null)continue;AssertRoot(key,root);AssertUnchanged(backup,key);}baseKey.DeleteSubKeyTree(backup.Path,false);
        }
        foreach(var backup in proof?.Namespace??[])
        {
            verifyContent();AssertUnshared(proof!.NamespaceId!,root.SyncRootId);using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
            using(var key=user.OpenSubKey(backup.Path)){if(key is null)continue;AssertUnchanged(backup,key);if(backup.Path.StartsWith(Namespace,StringComparison.Ordinal))AssertNamespace(key,root);else AssertClass(key,root);}
            user.DeleteSubKeyTree(backup.Path,false);
        }
        foreach(var value in proof?.Hidden??[])
        {
            verifyContent();AssertHidden(value);using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var key=user.OpenSubKey(value.Path,true);
            key?.DeleteValue(value.Value.Name,false); // 共享键及其他命名值保持。
        }
        var remaining=Verify(root,proof);if(remaining.Length!=0)throw new IOException("Shell注册仍有残留："+string.Join("；",remaining));
    }
    public string[] Verify(OrphanedMountCandidate root,RecoveryShellProof? proof)=>Remaining(root,proof);
    internal string[] Remaining(OrphanedMountCandidate root,RecoveryShellProof? proof)
    {
        ValidateRoot(root);ValidateProof(root,proof);var remaining=new List<string>();
        foreach(var hive in Hives)if(Exists(hive,Manager+"\\"+root.SyncRootId))remaining.Add(hive+"\\"+Manager+"\\"+root.SyncRootId);
        // 即使原备份未捕获Namespace，也重新发现此根的每根入口，防止包内空快照被当作成功。
        foreach(var hint in List().Where(x=>x.Id==root.SyncRootId))remaining.Add("入口："+hint.Path+" "+hint.NamespaceId);
        foreach(var discoveredGuid in NamespaceIds(root.SyncRootId))remaining.Add("HKCU\\"+Namespace+discoveredGuid);
        if(proof?.NamespaceId is {} guid)
        {
            foreach(var path in new[]{Namespace+guid,Classes+guid})if(Exists(RegistryHive.CurrentUser,path))remaining.Add("HKCU\\"+path);
            using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
            foreach(var path in HiddenPaths){using var key=user.OpenSubKey(path);if(key?.GetValueNames().Contains(guid,StringComparer.OrdinalIgnoreCase)==true)remaining.Add("HKCU\\"+path+" ["+guid+"]");}
        }
        return remaining.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private IEnumerable<string> NamespaceIds(string id)
    {
        using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var spaces=user.OpenSubKey(Namespace.TrimEnd(Path.DirectorySeparatorChar));
        if(spaces is null)yield break;
        foreach(var name in spaces.GetSubKeyNames())
        {using var entry=spaces.OpenSubKey(name);if(entry?.GetValue("") as string==id&&Guid.TryParseExact(name,"B",out var guid)&&guid!=Guid.Empty)yield return guid.ToString("B").ToUpperInvariant();}
    }
    private void ValidateProof(OrphanedMountCandidate root,RecoveryShellProof? proof)
    {
        if(proof is null)return;var guid=NormalizeGuid(proof.NamespaceId);
        foreach(var backup in proof.Roots)if(!Hives.Contains(backup.Hive)||backup.Path!=Manager+"\\"+root.SyncRootId)throw new IOException("Shell根恢复键越界。");
        foreach(var backup in proof.Namespace)if(guid is null||backup.Hive!=RegistryHive.CurrentUser||backup.Path!=Namespace+guid&&backup.Path!=Classes+guid)throw new IOException("每根Shell恢复键越界。");
        foreach(var value in proof.Hidden??[])if(guid is null||!HiddenPaths.Contains(value.Path,StringComparer.Ordinal)||!value.Value.Name.Equals(guid,StringComparison.OrdinalIgnoreCase))throw new IOException("Shell隐藏值恢复范围越界。");
        if(proof.External is not null)throw new IOException("本地注册操作拒绝嵌套外部证明。");
    }
    private void AssertRoot(RegistryKey key,OrphanedMountCandidate root)
    {
        using var users=key.OpenSubKey("UserSyncRoots");
        if(users is null||users.GetValueNames().Length!=1||users.GetValueNames()[0]!=_sid||users.GetValue(_sid) is not string path||!SamePath(path,root.LocalPath))throw new IOException("Shell根存在其他用户或路径，拒绝清理。");
        if(Owner(key)!=_sid)throw new IOException("Shell根注册的所有者不是当前用户。");
    }
    private static void AssertNamespace(RegistryKey key,OrphanedMountCandidate root)
    {if(key.GetValue("") is not string id||id!=root.SyncRootId||key.SubKeyCount!=0)throw new IOException("每根Shell入口归属不匹配。");}
    private static void AssertClass(RegistryKey key,OrphanedMountCandidate root)
    {using var bag=key.OpenSubKey(@"Instance\InitPropertyBag");if(bag?.GetValue("TargetFolderPath") is not string path||!SamePath(path,root.LocalPath))throw new IOException("每根Shell类目标路径不匹配。");}
    private static string Owner(RegistryKey key)=>key.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))!.Value;
    private static string? NormalizeGuid(string? raw)=>raw is null?null:Guid.TryParseExact(raw,"B",out var guid)&&guid!=Guid.Empty?guid.ToString("B").ToUpperInvariant():throw new IOException("每根命名空间GUID无效。");
    private static bool SamePath(string a,string b)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),StringComparison.OrdinalIgnoreCase);
    private static bool Exists(RegistryHive hive,string path){using var baseKey=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);using var key=baseKey.OpenSubKey(path);return key is not null;}
    private void AssertUnshared(string guid,string own)
    {
        foreach(var hive in Hives)
        {using var baseKey=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);using var manager=baseKey.OpenSubKey(Manager);if(manager is null)continue;
            foreach(var id in manager.GetSubKeyNames())
            {if(id==own)continue;using var key=manager.OpenSubKey(id);if(string.Equals(key?.GetValue("NamespaceCLSID") as string,guid,StringComparison.OrdinalIgnoreCase))throw new IOException("该Shell类被其他根引用，拒绝清理。");}}
    }
    private static RecoveryRegistryBackup Backup(RegistryHive hive,string path,RegistryKey key)=>new(hive,path,Owner(key),ReadTree(key));
    private void AssertUnchanged(RecoveryRegistryBackup backup,RegistryKey key)
    {if(backup.Owner!=_sid||Owner(key)!=_sid||JsonSerializer.Serialize(backup.Tree)!=JsonSerializer.Serialize(ReadTree(key)))throw new IOException("注册快照或所有者变化，保留现场和备份。");}
    private void AssertHidden(RecoveryHiddenValue value)
    {
        using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var key=user.OpenSubKey(value.Path);
        if(key is null||!key.GetValueNames().Contains(value.Value.Name,StringComparer.OrdinalIgnoreCase))return;
        if(Owner(key)!=_sid||value.Owner!=_sid||JsonSerializer.Serialize(ReadValue(key,value.Value.Name))!=JsonSerializer.Serialize(value.Value))throw new IOException("隐藏值或所有者变化，保留共享键。");
    }
    private static RecoveryRegistryValue ReadValue(RegistryKey key,string name)
    {
        var raw=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);var kind=key.GetValueKind(name);
        var data=raw switch{byte[] bytes=>new[]{Convert.ToBase64String(bytes)},string[] strings=>strings,string value=>[value],int number=>[number.ToString(CultureInfo.InvariantCulture)],long number=>[number.ToString(CultureInfo.InvariantCulture)],_=>throw new IOException("注册值类型未知。")};
        if(data.Sum(x=>x.Length)>128*1024)throw new IOException("注册值过长。");return new(name,kind,data);
    }
    /// <summary>有界备份原始类型和值；总消息另受私有协议与日志一MiB限制。</summary>
    private static RecoveryRegistryTree ReadTree(RegistryKey key,int depth=0)
    {
        if(depth>10||key.ValueCount>128||key.SubKeyCount>128)throw new IOException("注册内容超出恢复限制。");
        var values=key.GetValueNames().Order(StringComparer.Ordinal).Select(name=>ReadValue(key,name)).ToArray();
        var children=new Dictionary<string,RecoveryRegistryTree>();foreach(var name in key.GetSubKeyNames().Order(StringComparer.Ordinal)){using var child=key.OpenSubKey(name)??throw new IOException("注册读取期间变化。");children.Add(name,ReadTree(child,depth+1));}
        return new(values,children);
    }
}
/// <summary>同一根同时捕获两视图；真实视图由独立入口处理并再次核验。任何不可用都保留待处理状态。</summary>
internal sealed class MaintenanceShellRegistration:IRecoveryShellRegistration
{
    private readonly RecoveryShellRegistration _local=new();
    private static RecoveryShellProof? Local(RecoveryShellProof? proof)=>proof is null?null:proof with{External=null};
    public IReadOnlyList<RecoveryRegistration> List()=>_local.List().Concat(ShellMaintenanceProcess.Send("discover").Roots??[]).DistinctBy(x=>(x.Id,x.Path,x.NamespaceId)).ToArray();
    public RecoveryShellProof Capture(OrphanedMountCandidate root)
    {
        var real=ShellMaintenanceProcess.Send("capture",root).Proof??throw new IOException("维护入口未返回真实注册备份。");
        var local=_local.Capture(root);return local with{External=real};
    }
    public void Unregister(string id)=>_local.Unregister(id);
    public void Cleanup(OrphanedMountCandidate root,RecoveryShellProof? proof,Action verifyContent)
    {
        if(proof?.External is null)throw new IOException("缺少真实视图备份，须先补齐备份再继续收尾。");
        verifyContent();ShellMaintenanceProcess.Send("cleanup",root,proof.External);verifyContent();_local.Cleanup(root,Local(proof),verifyContent);
        if(Verify(root,proof) is {Length:>0} remaining)throw new IOException("包外核验仍有残留："+string.Join("；",remaining));
    }
    public string[] Verify(OrphanedMountCandidate root,RecoveryShellProof? proof)
        =>_local.Verify(root,Local(proof)).Select(x=>"包内："+x).Concat((ShellMaintenanceProcess.Send("verify",root,proof?.External??Local(proof)).Remaining??throw new IOException("缺少包外核验结果。")).Select(x=>"真实HKCU："+x)).ToArray();
}
