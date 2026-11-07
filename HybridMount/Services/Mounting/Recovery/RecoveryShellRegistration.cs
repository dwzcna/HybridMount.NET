using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Globalization;
using HybridMount.Models.Mounting;
using Microsoft.Win32;
using Windows.Storage.Provider;

namespace HybridMount.Services.Mounting;

internal sealed record RecoveryRegistration(string Id,string Path);
internal sealed record RecoveryRegistryValue(string Name,RegistryValueKind Kind,string[] Data);
internal sealed record RecoveryRegistryTree(RecoveryRegistryValue[] Values,Dictionary<string,RecoveryRegistryTree> Children);
internal sealed record RecoveryRegistryBackup(RegistryHive Hive,string Path,string Owner,RecoveryRegistryTree Tree);
internal sealed record RecoveryShellProof(RecoveryRegistryBackup[] Roots,RecoveryRegistryBackup[] Namespace,string? NamespaceId);
/// <summary>隔离系统注册访问边界，测试可以模拟WinRT不可见和注销部分失败。</summary>
internal interface IRecoveryShellRegistration
{
    IReadOnlyList<RecoveryRegistration> List();
    RecoveryShellProof? Capture(OrphanedMountCandidate root);
    void Unregister(string id);
    void Cleanup(OrphanedMountCandidate root,RecoveryShellProof? proof,Action verifyEmpty);
}
/// <summary>将WinRT及Shell持久项只作为发现线索；原生身份由恢复服务核验。只有精确归属且已备份的每根注册可收尾。</summary>
internal sealed class RecoveryShellRegistration:IRecoveryShellRegistration
{
    private const string Manager=@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
    private const string Namespace=@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\";
    private const string Classes=@"Software\Classes\CLSID\";
    private readonly string _sid=WindowsIdentity.GetCurrent().User!.Value;
    private static readonly RegistryHive[] Hives=[RegistryHive.LocalMachine,RegistryHive.CurrentUser];
    public IReadOnlyList<RecoveryRegistration> List()
    {
        var found=new List<RecoveryRegistration>();
        // WinRT可见性受包身份影响；注册表仅限本产品和当前SID，不枚举卷内容。
        try{foreach(var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())if(OrphanedMountRecovery.TryParseId(root.Id,_sid,out _)&&root.Path?.Path is {} path)found.Add(new(root.Id,path));}
        catch(Exception error)when(error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException){}
        foreach(var hive in Hives)
        {
            using var baseKey=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);using var manager=baseKey.OpenSubKey(Manager);if(manager is null)continue;
            foreach(var name in manager.GetSubKeyNames())
            {
                if(!OrphanedMountRecovery.TryParseId(name,_sid,out _))continue;
                using var root=manager.OpenSubKey(name);using var users=root?.OpenSubKey("UserSyncRoots");
                if(users?.GetValue(_sid, null,RegistryValueOptions.DoNotExpandEnvironmentNames) is string path)found.Add(new(name,path));
            }
        }
        return found.DistinctBy(x=>(x.Id,x.Path)).ToArray();
    }
    public RecoveryShellProof Capture(OrphanedMountCandidate root)
    {
        var roots=new List<RecoveryRegistryBackup>();string? namespaceId=null;
        foreach(var hive in Hives)
        {
            using var baseKey=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);using var key=baseKey.OpenSubKey(Manager+"\\"+root.SyncRootId);if(key is null)continue;
            AssertRoot(key,root);roots.Add(Backup(hive,Manager+"\\"+root.SyncRootId,key));
            if(key.GetValue("NamespaceCLSID") is string raw)
            {
                if(!Guid.TryParse(raw,out var guid)||guid==Guid.Empty)throw new IOException("每根命名空间GUID无效。");
                var normalized=guid.ToString("B").ToUpperInvariant();
                if(namespaceId is not null&&namespaceId!=normalized)throw new IOException("多个注册的命名空间不一致。");namespaceId=normalized;
            }
        }
        var names=new List<RecoveryRegistryBackup>();
        if(namespaceId is not null)
        {
            AssertUnshared(namespaceId,root.SyncRootId);
            using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
            using var space=user.OpenSubKey(Namespace+namespaceId);using var cls=user.OpenSubKey(Classes+namespaceId);
            if(space is not null)
            {if(space.GetValue("") is not string value||value!=root.SyncRootId||space.SubKeyCount!=0)throw new IOException("每根Shell入口归属不匹配。");names.Add(Backup(RegistryHive.CurrentUser,Namespace+namespaceId,space));}
            if(cls is not null)
            {
                using var bag=cls.OpenSubKey(@"Instance\InitPropertyBag");
                if(bag?.GetValue("TargetFolderPath") is not string target||!SamePath(target,root.LocalPath))throw new IOException("每根Shell类目标路径不匹配。");
                // 系统共享CLSID不会有这个精确每根入口与目标；仍要求配对入口或已验证原根的NamespaceCLSID。
                names.Add(Backup(RegistryHive.CurrentUser,Classes+namespaceId,cls));
            }
        }
        return new(roots.ToArray(),names.ToArray(),namespaceId);
    }
    public void Unregister(string id)=>StorageProviderSyncRootManager.Unregister(id);
    public void Cleanup(OrphanedMountCandidate root,RecoveryShellProof? proof,Action verifyEmpty)
    {
        if(!OrphanedMountRecovery.TryParseId(root.SyncRootId,_sid,out var id)||id!=root.MountId)throw new IOException("Shell恢复ID不属于当前用户。");
        verifyEmpty();
        foreach(var backup in proof?.Roots??[])
        {
            if(backup.Path!=Manager+"\\"+root.SyncRootId||!Hives.Contains(backup.Hive))throw new IOException("Shell恢复日志键范围不合法。");
            using var baseKey=RegistryKey.OpenBaseKey(backup.Hive,RegistryView.Registry64);using(var key=baseKey.OpenSubKey(backup.Path)){if(key is null)continue;AssertRoot(key,root);AssertUnchanged(backup,key);}
            verifyEmpty();baseKey.DeleteSubKeyTree(backup.Path,false);
        }
        if(Hives.Any(hive=>Exists(hive,Manager+"\\"+root.SyncRootId)))throw new IOException("Shell注册仍在，已保留备份，无法确认归属或权限。");
        if(proof?.NamespaceId is {} namespaceId)
        {
            if(!Guid.TryParseExact(namespaceId,"B",out var guid)||guid==Guid.Empty)throw new IOException("恢复日志命名空间无效。");
            foreach(var backup in proof.Namespace)
            {
                if(backup.Hive!=RegistryHive.CurrentUser||backup.Path!=Namespace+namespaceId&&backup.Path!=Classes+namespaceId)throw new IOException("每根Shell恢复键越界。");
                verifyEmpty();AssertUnshared(namespaceId,root.SyncRootId);
                using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
                using(var key=user.OpenSubKey(backup.Path))
                {
                    if(key is null)continue;AssertUnchanged(backup,key);
                    if(backup.Path.StartsWith(Namespace,StringComparison.Ordinal))
                    {if(key.GetValue("") as string!=root.SyncRootId)throw new IOException("Shell入口身份变化。");}
                    else{using var bag=key.OpenSubKey(@"Instance\InitPropertyBag");if(bag?.GetValue("TargetFolderPath") is not string path||!SamePath(path,root.LocalPath))throw new IOException("Shell目标身份变化。");}
                }
                user.DeleteSubKeyTree(backup.Path,false);
            }
        }
        if(List().Any(x=>x.Id==root.SyncRootId))throw new IOException("WinRT或Shell仍返回此根，保留注销阶段供重试。");
    }
    private void AssertRoot(RegistryKey key,OrphanedMountCandidate root)
    {
        using var users=key.OpenSubKey("UserSyncRoots");
        if(users is null||users.GetValueNames().Length!=1||users.GetValueNames()[0]!=_sid||users.GetValue(_sid) is not string path||!SamePath(path,root.LocalPath))throw new IOException("Shell根存在其他用户或路径，拒绝清理。");
        if(Owner(key)!=_sid)throw new IOException("Shell根注册的所有者不是当前用户。");
    }
    private static string Owner(RegistryKey key)=>key.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))!.Value;
    private static bool SamePath(string a,string b)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(b,StringComparison.OrdinalIgnoreCase);
    private static bool Exists(RegistryHive hive,string path){using var baseKey=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);using var key=baseKey.OpenSubKey(path);return key is not null;}
    private void AssertUnshared(string guid,string own)
    {
        foreach(var hive in Hives)
        {
            using var baseKey=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);using var manager=baseKey.OpenSubKey(Manager);if(manager is null)continue;
            foreach(var id in manager.GetSubKeyNames())
            {if(id==own)continue;using var key=manager.OpenSubKey(id);if(string.Equals(key?.GetValue("NamespaceCLSID") as string,guid,StringComparison.OrdinalIgnoreCase))throw new IOException("该Shell类被其他根引用，拒绝清理。");}
        }
    }
    private static RecoveryRegistryBackup Backup(RegistryHive hive,string path,RegistryKey key)=>new(hive,path,Owner(key),ReadTree(key));
    private void AssertUnchanged(RecoveryRegistryBackup backup,RegistryKey key)
    {if(backup.Owner!=_sid||Owner(key)!=_sid||JsonSerializer.Serialize(backup.Tree)!=JsonSerializer.Serialize(ReadTree(key)))throw new IOException("注册快照或所有者变化，保留现场和备份。");}
    /// <summary>有界保存每个精确键的原始值／类型；持久化完成后才允许注销和删除。</summary>
    private static RecoveryRegistryTree ReadTree(RegistryKey key,int depth=0)
    {
        if(depth>10||key.ValueCount>128||key.SubKeyCount>128)throw new IOException("注册内容超出恢复限制。");
        var values=new List<RecoveryRegistryValue>();
        foreach(var name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            var raw=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);var kind=key.GetValueKind(name);
            var data=raw switch{byte[] bytes=>new[]{Convert.ToBase64String(bytes)},string[] strings=>strings,string value=>[value],int number=>[number.ToString(CultureInfo.InvariantCulture)],long number=>[number.ToString(CultureInfo.InvariantCulture)],_=>throw new IOException("注册值类型未知。")};
            if(data.Sum(x=>x.Length)>128*1024)throw new IOException("注册值过长。");values.Add(new(name,kind,data));
        }
        var children=new Dictionary<string,RecoveryRegistryTree>();foreach(var name in key.GetSubKeyNames().Order(StringComparer.Ordinal)){using var child=key.OpenSubKey(name)??throw new IOException("注册读取期间变化。");children.Add(name,ReadTree(child,depth+1));}
        return new(values.ToArray(),children);
    }
}
