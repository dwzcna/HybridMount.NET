using HybridMount.Models.Mounting;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace HybridMount.Services.Mounting;

/// <summary>只用 WinRT 注册入口建立持久同步根；CFAPI 负责连接，常规退出不注销。</summary>
public static class CloudRootRegistration
{
    public static string Id(MountDefinition mount) => $"HybridMount.NET!{System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value}!{mount.Id:N}";
    public static async Task EnsureAsync(MountDefinition mount, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = MountPathGuard.ReadRoot(mount.LocalPath);
        if (current is not null)
        { if (current.MountId != mount.Id) throw new IOException("同步根属于其他挂载。"); return; }
        foreach (var registered in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            var other = registered.Path?.Path;
            if (!string.IsNullOrEmpty(other) && (MountPathGuard.IsUnder(other,mount.LocalPath) || MountPathGuard.IsUnder(mount.LocalPath,other)))
                throw new IOException("目标与已有同步根重叠，不能注册。");
        }
        var info = new StorageProviderSyncRootInfo
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
        token.ThrowIfCancellationRequested(); StorageProviderSyncRootManager.Register(info);
        if (MountPathGuard.ReadRoot(mount.LocalPath)?.MountId != mount.Id) throw new IOException("同步根注册结果未通过身份核对。");
    }
    public static void Unregister(MountDefinition mount)
    {
        MountPathGuard.ValidateOperation(mount,mount.LocalPath);
        StorageProviderSyncRootManager.Unregister(Id(mount));
    }
}
