using HybridMount.Models.Mounting;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace HybridMount.Services.Mounting;

/// <summary>只用 WinRT 注册入口建立持久同步根；CFAPI 负责连接，常规退出不注销。</summary>
public static class CloudRootRegistration
{
    public static string Id(MountDefinition mount) => $"HybridMount.NET!{System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value}!{mount.Id:N}";
    /// <summary>核对根身份、原生注册和 WinRT 注册。归属冲突或查询失败一律拒绝破坏性操作。</summary>
    /// <returns>属于当前用户和挂载时为真；确认无注册时为假。</returns>
    public static bool IsOwnedRoot(MountDefinition mount)
    {
        if (mount.RootIdentity != MountPathGuard.FileIdentity(mount.LocalPath)) throw new IOException("挂载根身份变化，保留配置待恢复。");
        var native = CloudFilesNative.RootIdentity(mount.LocalPath, strict: true);
        var marker = native is null ? null : MountPathGuard.ReadRoot(mount.LocalPath);
        if (native is not null && (marker?.MountId != mount.Id || marker.Owner != System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value))
            throw new IOException("同步根属于其他提供程序或用户，拒绝操作。");
        var listed = false;
        foreach (var registration in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            var path = registration.Path?.Path;
            if (string.IsNullOrEmpty(path) || !(MountPathGuard.IsUnder(path,mount.LocalPath) || MountPathGuard.IsUnder(mount.LocalPath,path))) continue;
            if (registration.Id != Id(mount) || !Path.TrimEndingDirectorySeparator(path).Equals(mount.LocalPath,StringComparison.OrdinalIgnoreCase))
                throw new IOException("目标与其他同步根重叠，拒绝操作。");
            listed = true;
        }
        if (listed && native is null) throw new IOException("系统注册记录不一致，保留配置待恢复。");
        return native is not null;
    }
    public static async Task EnsureAsync(MountDefinition mount, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(mount.RootIdentity!=MountPathGuard.FileIdentity(mount.LocalPath))throw new IOException("注册前根身份发生变化，保留配置。");
        var current = MountPathGuard.ReadRoot(mount.LocalPath);
        if (current is not null)
        { if (current.MountId != mount.Id) throw new IOException("同步根属于其他挂载。"); return; }
        foreach (var registered in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            var other = registered.Path?.Path;
            if (!string.IsNullOrEmpty(other) && (MountPathGuard.IsUnder(other,mount.LocalPath) || MountPathGuard.IsUnder(mount.LocalPath,other)))
                throw new IOException("目标与已有同步根重叠，不能注册。");
        }
        var info=await CreateDescriptorAsync(mount);
        AddIssueDefinition(info);
        token.ThrowIfCancellationRequested();
        if(mount.RootIdentity!=MountPathGuard.FileIdentity(mount.LocalPath))throw new IOException("注册前根身份发生变化，保留配置。");
        StorageProviderSyncRootManager.Register(info);
        MountPathGuard.ValidateOperation(mount,mount.LocalPath);
    }
    /// <summary>在已有根上更新同一注册描述符；不注销、不转换文件或改变根身份。旧WinRT查询不可见时用本程序的原策略重建描述符。</summary>
    internal static async Task EnsureIssueDefinitionAsync(MountDefinition mount,CancellationToken token)
    {
        if(!IsOwnedRoot(mount))throw new IOException("不能为未注册的根发布同步问题。");
        var info=StorageProviderSyncRootManager.GetCurrentSyncRoots().FirstOrDefault(x=>x.Id==Id(mount));
        if(info?.StorageProviderItemPropertyDefinitions.Any(x=>x.Id==CloudUploadIssueProperty.PropertyId)==true)return;
        info??=await CreateDescriptorAsync(mount);
        AddIssueDefinition(info);
        token.ThrowIfCancellationRequested();MountPathGuard.ValidateOperation(mount,mount.LocalPath);
        StorageProviderSyncRootManager.Register(info);
        MountPathGuard.ValidateOperation(mount,mount.LocalPath);
    }
    /// <summary>本产品新旧根共用相同描述符，升级不改变路径、Context或CFAPI策略。</summary>
    private static async Task<StorageProviderSyncRootInfo> CreateDescriptorAsync(MountDefinition mount)
    {
        return new StorageProviderSyncRootInfo
        {
            Id = Id(mount), Path = await StorageFolder.GetFolderFromPathAsync(mount.LocalPath),
            DisplayNameResource = mount.Name, IconResource = Path.Combine(AppContext.BaseDirectory,"HybridMount.NET.exe")+",0",
            HydrationPolicy = StorageProviderHydrationPolicy.Partial, PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime |
                StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.DirectoryCreationTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None, Version = typeof(MountManager).Assembly.GetName().Version!.ToString(), ShowSiblingsAsGroup = false,
            Context = CryptographicBuffer.CreateFromByteArray(MountPathGuard.RootBytes(mount.Id))
        };
    }
    private static void AddIssueDefinition(StorageProviderSyncRootInfo info)
    {
        if(!info.StorageProviderItemPropertyDefinitions.Any(x=>x.Id==CloudUploadIssueProperty.PropertyId))
            info.StorageProviderItemPropertyDefinitions.Add(new(){Id=CloudUploadIssueProperty.PropertyId,DisplayNameResource="同步问题"});
    }
    public static void Unregister(MountDefinition mount)
    {
        MountPathGuard.ValidateOperation(mount,mount.LocalPath);
        StorageProviderSyncRootManager.Unregister(Id(mount));
    }
}
