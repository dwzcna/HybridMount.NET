using HybridMount.Models.Mounting;
namespace HybridMount.Models.Thumbnails;

/// <summary>每挂载一个SQLite文件；只按GUID路由，旧共享库逐根迁移，移除不扫描或删除其他挂载。</summary>
internal sealed class ThumbnailCacheCatalog(string directory)
{
    private readonly string _directory=directory; // 路径验证延迟至实际启用后的访问，关闭门控不触碰输入路径。
    internal string PathFor(Guid mount)=>ThumbnailCachePaths.ForMount(_directory,mount);
    private ThumbnailCacheStore Store(Guid mount)=>new(PathFor(mount));
    private ThumbnailCacheStore Legacy=>new(Path.Combine(_directory,"thumbnails.db"));
    internal ThumbnailCacheRoot Configure(ThumbnailCacheRoot requested)
    {
        var store=Store(requested.MountId);
        if(Legacy.UsageBytes!=0&&store.Root(requested.MountId) is null&&Legacy.CopyMountTo(store,requested) is {} copied)
        {if(Legacy.RemoveRoot(copied)&&Legacy.IsEmpty)Legacy.DeleteDatabase(null);}
        return store.Configure(requested,singleMount:true);
    }
    private ThumbnailCacheStore? StoreFor(string path)
    {
        var root=MountPathGuard.ReadRoot(path);return root is not null&&root.Owner==ThumbnailCacheReader.CurrentOwner?Store(root.MountId):null;
    }
    internal byte[]? Read(string path,int size,Func<ThumbnailCacheBinding,bool> validate)=>StoreFor(path)?.Read(path,size,validate);
    internal bool HasFrames(ThumbnailCacheBinding binding)=>Store(binding.Root.MountId).HasFrames(binding.Path,current=>current.ContentKey==binding.ContentKey&&ThumbnailCacheReader.Valid(current));
    internal ThumbnailCacheBinding? Binding(string path)=>StoreFor(path)?.Binding(path);
    internal bool Publish(ThumbnailCacheBinding binding,IReadOnlyDictionary<int,byte[]> frames)=>Store(binding.Root.MountId).Publish(binding,frames);
    internal bool Rebind(ThumbnailCacheBinding previous,ThumbnailCacheBinding current)=>Store(current.Root.MountId).Rebind(previous,current);
    internal void Invalidate(Guid mount,string? entry=null){var store=Store(mount);if(store.UsageBytes!=0)store.Invalidate(mount,entry);}
    /// <summary>已停队列后核对当前账户与根快照；缓存删除失败必须保留挂载配置供继续收尾。</summary>
    internal void Remove(MountDefinition mount)
    {
        void Validate(ThumbnailCacheRoot root)
        {if(root.MountId!=mount.Id||root.Owner!=ThumbnailCacheReader.CurrentOwner||!Path.GetFullPath(root.Path).Equals(Path.GetFullPath(mount.LocalPath),StringComparison.OrdinalIgnoreCase)||mount.RootIdentity is not null&&root.FileId!=mount.RootIdentity)throw new IOException("缩略图库不属于待移除挂载，未删除。");}
        var store=Store(mount.Id);var root=store.Root(mount.Id);if(root is not null)Validate(root);
        if(store.UsageBytes!=0)store.DeleteDatabase(root);
        var old=Legacy.Root(mount.Id);if(old is not null){Validate(old);if(!Legacy.RemoveRoot(old))throw new IOException("旧缩略图库迁移记录变化，未清理。");if(Legacy.IsEmpty)Legacy.DeleteDatabase(null);}
    }
    private IEnumerable<ThumbnailCacheStore> Stores()
    {
        if(!Directory.Exists(_directory))yield break;
        foreach(var path in Directory.EnumerateFiles(_directory,"*.db")){var name=Path.GetFileNameWithoutExtension(path);if(Guid.TryParseExact(name,"N",out _)||name=="thumbnails")yield return new(path);}
    }
    internal long UsageBytes=>Stores().Sum(store=>store.UsageBytes);
    internal long Trim(long needed)
    {long removed=0;foreach(var store in Stores()){if(removed>=needed)break;removed+=store.Trim(needed-removed);}return removed;}
}
