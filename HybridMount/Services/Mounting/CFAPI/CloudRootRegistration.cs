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
    public static async Task EnsureAsync(MountDefinition mount, CancellationToken token,Action<Exception>? iconRefreshFailed=null)
    {
        token.ThrowIfCancellationRequested();
        if(mount.RootIdentity!=MountPathGuard.FileIdentity(mount.LocalPath))throw new IOException("注册前根身份发生变化，保留配置。");
        var current = MountPathGuard.ReadRoot(mount.LocalPath);
        if (current is not null)
        {
            RefreshExistingIcon(mount.Id,current.MountId,()=>
            {
                if(!IsOwnedRoot(mount))throw new IOException("同步根归属无法确认，保留图标注册。");
                var info=StorageProviderSyncRootManager.GetCurrentSyncRoots().FirstOrDefault(x=>x.Id==Id(mount));
                if(info is null)throw new IOException("当前身份无法读取同步根描述符，保留图标注册。");
                if(info.Path is null||!Path.TrimEndingDirectorySeparator(info.Path.Path).Equals(mount.LocalPath,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("同步根描述符路径不匹配，保留图标注册。");
                return info;
            },info=>
            {
                MountPathGuard.ValidateOperation(mount,mount.LocalPath);
                StorageProviderSyncRootManager.Register(info);
            },iconRefreshFailed??(error=>System.Diagnostics.Trace.TraceWarning("同步根图标刷新失败："+error.Message)));
            return;
        }
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
    internal static string ExpectedIconResource=>Path.Combine(AppContext.BaseDirectory,"HybridMount.NET.exe")+",0";
    internal static void RefreshExistingIcon(Guid mountId,Guid registeredMountId,Func<StorageProviderSyncRootInfo> read,
        Action<StorageProviderSyncRootInfo> register,Action<Exception> failed)
    {
        if(registeredMountId!=mountId)throw new IOException("同步根属于其他挂载。");
        try
        {
            var existing=read();
            if(string.Equals(existing.IconResource,ExpectedIconResource,StringComparison.OrdinalIgnoreCase))return;
            register(CloneWithCurrentIcon(existing));
        }
        catch(Exception error){try{failed(error);}catch{}}
    }
    internal static StorageProviderSyncRootInfo CloneWithCurrentIcon(StorageProviderSyncRootInfo source)
    {
        var copy=new StorageProviderSyncRootInfo
        {
            Id=source.Id,DisplayNameResource=source.DisplayNameResource,IconResource=ExpectedIconResource,
            Context=source.Context,Version=source.Version,AllowPinning=source.AllowPinning,
            HydrationPolicy=source.HydrationPolicy,HydrationPolicyModifier=source.HydrationPolicyModifier,
            PopulationPolicy=source.PopulationPolicy,InSyncPolicy=source.InSyncPolicy,HardlinkPolicy=source.HardlinkPolicy,
            ProtectionMode=source.ProtectionMode,ProviderId=source.ProviderId,RecycleBinUri=source.RecycleBinUri,
            ShowSiblingsAsGroup=source.ShowSiblingsAsGroup
        };
        if(source.Path is {} path)copy.Path=path;
        foreach(var definition in source.StorageProviderItemPropertyDefinitions)copy.StorageProviderItemPropertyDefinitions.Add(definition);
        if(Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Storage.Provider.StorageProviderSyncRootInfo","FallbackFileTypeInfo"))
            foreach(var fileType in source.FallbackFileTypeInfo)copy.FallbackFileTypeInfo.Add(fileType);
        return copy;
    }
    /// <summary>在已有根上更新同一注册描述符；不注销、不转换文件或改变根身份。旧WinRT查询不可见时用本程序的原策略重建描述符。</summary>
    internal static Task EnsureIssueDefinitionAsync(MountDefinition mount,CancellationToken token)=>EnsureDefinitionAsync(mount,CloudUploadIssueProperty.PropertyId,"同步问题",token);
    /// <summary>只增加排除状态的系统属性定义；保留已有根策略和其他定义，不注册菜单、缩略图或COM处理器。</summary>
    internal static Task EnsureExclusionDefinitionAsync(MountDefinition mount,CancellationToken token)=>EnsureDefinitionAsync(mount,CloudItemStateProperty.ExclusionId,"上传排除",token);
    private static async Task EnsureDefinitionAsync(MountDefinition mount,int id,string title,CancellationToken token)
    {
        if(!IsOwnedRoot(mount))throw new IOException("不能为未注册的根发布同步问题。");
        var info=StorageProviderSyncRootManager.GetCurrentSyncRoots().FirstOrDefault(x=>x.Id==Id(mount));
        if(info?.StorageProviderItemPropertyDefinitions.Any(x=>x.Id==id)==true)return;
        info??=await CreateDescriptorAsync(mount);
        if(!info.StorageProviderItemPropertyDefinitions.Any(x=>x.Id==id))info.StorageProviderItemPropertyDefinitions.Add(new(){Id=id,DisplayNameResource=title});
        token.ThrowIfCancellationRequested();MountPathGuard.ValidateOperation(mount,mount.LocalPath);
        StorageProviderSyncRootManager.Register(info);
        MountPathGuard.ValidateOperation(mount,mount.LocalPath);
    }
    /// <summary>两种运行模式共用新根描述符并允许原生固定；已有根不因本版重新注册或改变CFAPI策略。</summary>
    internal static async Task<StorageProviderSyncRootInfo> CreateDescriptorAsync(MountDefinition mount)
    {
        return new StorageProviderSyncRootInfo
        {
            Id = Id(mount), Path = await StorageFolder.GetFolderFromPathAsync(mount.LocalPath),
            DisplayNameResource = mount.Name, IconResource = ExpectedIconResource,
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
        StorageProviderSyncRootManager.Unregister(Id(mount));
    }
}
