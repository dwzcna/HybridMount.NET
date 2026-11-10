using HybridMount.Models.Mounting;
using global::Windows.Security.Cryptography;
using global::Windows.Storage;
using global::Windows.Storage.Provider;

namespace HybridMount.Models.Mounting.Windows;

/// <summary>通过原生后台注册持久同步根；只读WinRT枚举保留兼容，常规退出不注销。</summary>
public static class CloudRootRegistration
{
    public static string Id(MountDefinition mount) => $"{ApplicationPaths.ProductScope}!{System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value}!{mount.Id:N}";
    /// <summary>核对根身份、原生注册和 WinRT 注册。归属冲突或查询失败一律拒绝破坏性操作。</summary>
    /// <returns>属于当前用户和挂载时为真；确认无注册时为假。</returns>
    public static bool IsOwnedRoot(MountDefinition mount)=>NativeRuntimeClient.Get().Invoke("ownedRoot",NativeRuntimeArguments.Root(mount)).GetProperty("registered").GetBoolean();
    /// <summary>原生后台核对NTFS、目录及本账户身份后注册；不跨进程传递CFAPI句柄。</summary>
    public static async Task EnsureAsync(MountDefinition mount,CancellationToken token)
    {
        await(await NativeRuntimeClient.GetAsync().ConfigureAwait(false)).InvokeAsync("registerRoot",NativeRuntimeArguments.Root(mount),token:token).ConfigureAwait(false);
        MountPathGuard.ValidateOperation(mount,mount.LocalPath);
    }
    /// <summary>在已有根上更新同一注册描述符；不注销、不转换文件或改变根身份。旧WinRT查询不可见时用本程序的原策略重建描述符。</summary>
    internal static Task EnsureIssueDefinitionAsync(MountDefinition mount,CancellationToken token)=>EnsureDefinitionAsync(mount,CloudUploadIssueProperty.PropertyId,"同步问题",token);
    /// <summary>只增加排除状态的系统属性定义；保留已有根策略和其他定义，不注册菜单、缩略图或COM处理器。</summary>
    internal static Task EnsureExclusionDefinitionAsync(MountDefinition mount,CancellationToken token)=>EnsureDefinitionAsync(mount,CloudItemStateProperty.ExclusionId,"上传排除",token);
    private static async Task EnsureDefinitionAsync(MountDefinition mount,int id,string title,CancellationToken token)
    {
        await(await NativeRuntimeClient.GetAsync().ConfigureAwait(false)).InvokeAsync("ensureDefinition",NativeRuntimeArguments.Root(mount,new{propertyId=id}),token:token).ConfigureAwait(false);
    }
    /// <summary>两种运行模式共用新根描述符并允许原生固定；已有根只更新主映像图标引用，保留CFAPI策略和身份。</summary>
    internal static async Task<StorageProviderSyncRootInfo> CreateDescriptorAsync(MountDefinition mount)
    {
        return new StorageProviderSyncRootInfo
        {
            Id = Id(mount), Path = await StorageFolder.GetFolderFromPathAsync(mount.LocalPath),
            DisplayNameResource = mount.Name, IconResource = Path.Combine(AppContext.BaseDirectory,"HybridMount.exe")+",0",
            HydrationPolicy = StorageProviderHydrationPolicy.Partial, PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime |
                StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.DirectoryCreationTime,
            AllowPinning = true, HardlinkPolicy = StorageProviderHardlinkPolicy.None, Version = typeof(MountManager).Assembly.GetName().Version!.ToString(), ShowSiblingsAsGroup = false,
            Context = CryptographicBuffer.CreateFromByteArray(MountPathGuard.RootBytes(mount.Id))
        };
    }
    private static void AddIssueDefinition(StorageProviderSyncRootInfo info)
    {
        if(!info.StorageProviderItemPropertyDefinitions.Any(x=>x.Id==CloudUploadIssueProperty.PropertyId))
            info.StorageProviderItemPropertyDefinitions.Add(new(){Id=CloudUploadIssueProperty.PropertyId,DisplayNameResource="同步问题"});
        if(!info.StorageProviderItemPropertyDefinitions.Any(x=>x.Id==CloudItemStateProperty.ExclusionId))
            info.StorageProviderItemPropertyDefinitions.Add(new(){Id=CloudItemStateProperty.ExclusionId,DisplayNameResource="上传排除"});
    }
    public static void Unregister(MountDefinition mount)
    {
        MountPathGuard.ValidateOperation(mount,mount.LocalPath);
        NativeRuntimeClient.Get().Invoke("unregisterRoot",NativeRuntimeArguments.Root(mount));
    }
}
