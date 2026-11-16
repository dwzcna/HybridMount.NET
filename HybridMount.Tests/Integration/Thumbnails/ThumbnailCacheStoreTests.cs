using HybridMount.Infrastructure.Thumbnails;
using Microsoft.Data.Sqlite;
namespace HybridMount.Tests.Integration.Thumbnails;

/// <summary>独立临时库验证BLOB、只读缺失、跨实例事务及epoch屏障；不读正式缓存或启动文件源。</summary>
public sealed class ThumbnailCacheStoreTests
{
    private static byte[] Png(string name="SyncIssue.png")=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets",name));
    private sealed class Area:IDisposable
    {
        internal readonly string Path=System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HybridMount.ThumbnailStore."+Guid.NewGuid().ToString("N")));
        internal ThumbnailCacheStore Store=>new(System.IO.Path.Combine(Path,"thumbnails.db"));
        internal ThumbnailCacheRoot Root=>new(Guid.Parse("9983bfeb-2f56-43f8-a7bf-b2dfddf7311e"),"C:\\isolated-root","root-file-id","owner","scope",true);
        internal ThumbnailCacheBinding Binding(ThumbnailCacheRoot root)=>new(root,"entry","C:\\isolated-root\\image.png","file-id",10,20,"stamp","revision",true);
        public void Dispose(){if(!System.IO.Path.GetDirectoryName(Path)!.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!System.IO.Path.GetFileName(Path).StartsWith("HybridMount.ThumbnailStore.",StringComparison.Ordinal))throw new IOException("测试清理路径不符。");if(Directory.Exists(Path))Directory.Delete(Path,true);}
    }
    [Fact]
    public void ReadMissDoesNotCreateDirectoriesOrDatabase()
    {using var area=new Area();Assert.Null(area.Store.Read("C:\\missing.png",64,_=>true));Assert.Null(area.Store.Root(area.Root.MountId));Assert.False(Directory.Exists(area.Path));}
    [Fact]
    public void PublishesAtomicBlobFramesAndReadHitDoesNotWriteImages()
    {
        using var area=new Area();var store=area.Store;var root=store.Configure(area.Root);var binding=area.Binding(root);var small=Png();var large=Png("SyncExcluded.png");
        Assert.True(store.Publish(binding,new Dictionary<int,byte[]>{{32,small},{128,large}}));
        Assert.Equal(small,store.Get(binding,32));Assert.Equal(large,store.Get(binding,64));Assert.Equal(large,store.Get(binding,256));
        var time=File.GetLastWriteTimeUtc(store.Database);for(var i=0;i<10;i++)Assert.Equal(small,store.Get(binding,16));Assert.Equal(time,File.GetLastWriteTimeUtc(store.Database));
        Assert.All(Directory.GetFiles(area.Path),file=>Assert.True(System.IO.Path.GetFileName(file) is "thumbnails.db" or "thumbnails.db-wal" or "thumbnails.db-shm"));
        Assert.Null(store.Get(binding with{FileId="other-file"},32));
    }
    [Fact]
    public void RootChangesAndDisableRejectLatePublicationButRestartKeepsEpoch()
    {
        using var area=new Area();var store=area.Store;var first=store.Configure(area.Root);var binding=area.Binding(first);Assert.True(store.Publish(binding,new Dictionary<int,byte[]>{{64,Png()}}));
        Assert.Equal(first,area.Store.Configure(area.Root));Assert.NotNull(area.Store.Get(binding,64));
        var changed=area.Store.Configure(area.Root with{Scope="new-scope"});Assert.Equal(first.Epoch+1,changed.Epoch);Assert.Null(store.Get(binding,64));Assert.False(store.Publish(binding,new Dictionary<int,byte[]>{{64,Png()}}));
        var current=area.Binding(changed);Assert.True(store.Publish(current,new Dictionary<int,byte[]>{{64,Png()}}));
        var disabled=store.Configure(changed with{Enabled=false});Assert.True(disabled.Epoch>changed.Epoch);Assert.False(store.Publish(current,new Dictionary<int,byte[]>{{64,Png()}}));Assert.Null(store.Get(current,64));
    }
    [Fact]
    public void InvalidationRemovesEverySizeAndSamePathReplacementDoesNotReuseOldIdentity()
    {
        using var area=new Area();var store=area.Store;var root=store.Configure(area.Root);var binding=area.Binding(root);var frames=new Dictionary<int,byte[]>{{32,Png()},{256,Png()}};
        store.Publish(binding,frames);store.Invalidate(root.MountId,binding.EntryId);Assert.Null(store.Get(binding,32));Assert.Null(store.Get(binding,256));
        var replacement=binding with{FileId="new-file",Revision="new-revision"};store.Publish(replacement,frames);Assert.Null(store.Get(binding,32));Assert.NotNull(store.Get(replacement,32));
        store.Trim(long.MaxValue);Assert.Null(store.Get(replacement,32));
    }
    [Fact]
    public void CurrentCatalogRemovalKeepsOtherMountsAndRejectsChangedIdentity()
    {
        using var area=new Area();var catalog=new ThumbnailCacheCatalog(area.Path);
        var first=catalog.Configure(area.Root with{Owner=ThumbnailCacheReader.CurrentOwner});
        var second=catalog.Configure(first with{MountId=Guid.NewGuid(),Path="C:/other-root"});
        var png=Png();catalog.Publish(area.Binding(first),new Dictionary<int,byte[]>{{64,png}});
        var other=area.Binding(second) with{Path="C:/other-root/image.png"};catalog.Publish(other,new Dictionary<int,byte[]>{{64,png}});
        Assert.Throws<IOException>(()=>catalog.Remove(new(first.MountId,"first",first.Path,RootIdentity:"changed")));
        Assert.True(File.Exists(catalog.PathFor(first.MountId)));
        catalog.Remove(new(first.MountId,"first",first.Path,RootIdentity:first.FileId));
        Assert.False(File.Exists(catalog.PathFor(first.MountId)));
        Assert.Equal(png,new ThumbnailCacheStore(catalog.PathFor(second.MountId)).Get(other,64));
    }
    [Fact]
    public void OldSharedDatabaseIsNeitherReadMigratedNorRemoved()
    {
        using var area=new Area();var old=area.Store;
        var root=old.Configure(area.Root with{Owner=ThumbnailCacheReader.CurrentOwner});
        var binding=area.Binding(root);var png=Png();Assert.True(old.Publish(binding,new Dictionary<int,byte[]>{{64,png}}));
        var before=File.ReadAllBytes(old.Database);
        var catalog=new ThumbnailCacheCatalog(area.Path);var current=catalog.Configure(root);
        var store=new ThumbnailCacheStore(catalog.PathFor(root.MountId));
        Assert.Null(store.Get(binding,64));Assert.Equal(png,old.Get(binding,64));
        Assert.True(store.Publish(binding with{Root=current},new Dictionary<int,byte[]>{{64,png}}));
        catalog.Remove(new(root.MountId,"current",root.Path,RootIdentity:root.FileId));
        Assert.False(File.Exists(store.Database));Assert.Equal(before,File.ReadAllBytes(old.Database));
    }
    [Fact]
    public void DatabaseDeletionRejectsChangedRootAndSharedContents()
    {
        using var area=new Area();var path=ThumbnailCachePaths.ForMount(area.Path,area.Root.MountId);var store=new ThumbnailCacheStore(path);var root=store.Configure(area.Root with{Owner=ThumbnailCacheReader.CurrentOwner});
        Assert.Throws<IOException>(()=>store.DeleteDatabase(root with{Epoch=root.Epoch+1}));Assert.True(File.Exists(path));
        store.Configure(root with{MountId=Guid.NewGuid(),Path="C:\\another"});Assert.Throws<IOException>(()=>store.DeleteDatabase(root));Assert.True(File.Exists(path));
    }
    [Fact]
    public void PerMountDatabaseRejectsForeignContentsBeforeConfigureOrMigration()
    {
        using var area=new Area();var requested=area.Root with{Owner=ThumbnailCacheReader.CurrentOwner};var catalog=new ThumbnailCacheCatalog(area.Path);
        var destination=new ThumbnailCacheStore(catalog.PathFor(requested.MountId));var foreign=destination.Configure(requested with{MountId=Guid.NewGuid(),Path="C:\\foreign"});
        Assert.Throws<IOException>(()=>catalog.Configure(requested));Assert.Null(destination.Root(requested.MountId));Assert.Equal(foreign,destination.Root(foreign.MountId));
        var legacy=area.Store;legacy.Configure(requested);Assert.Throws<IOException>(()=>catalog.Configure(requested));Assert.Equal(requested,legacy.Root(requested.MountId));Assert.Equal(foreign,destination.Root(foreign.MountId));
    }
    [Fact]
    public void UnrelatedLegacyScopeIsNeverImportedOrDeleted()
    {
        using var area=new Area();var legacy=area.Store;var root=legacy.Configure(area.Root);var png=Png();var binding=area.Binding(root);legacy.Publish(binding,new Dictionary<int,byte[]>{{64,png}});
        var catalog=new ThumbnailCacheCatalog(area.Path);var current=catalog.Configure(root with{Scope="new-connection"});Assert.Null(new ThumbnailCacheStore(catalog.PathFor(root.MountId)).Get(binding,64));Assert.NotNull(legacy.Get(binding,64));Assert.Equal("new-connection",current.Scope);
    }
    [Fact]
    public async Task IndependentReadersNeverObservePartialPublication()
    {
        using var area=new Area();var root=area.Store.Configure(area.Root);var binding=area.Binding(root);var png=Png();
        var writes=Task.Run(()=>{for(var i=0;i<25;i++)Assert.True(area.Store.Publish(binding,new Dictionary<int,byte[]>{{32,png},{256,png}}));});
        for(var i=0;i<50;i++){var current=area.Store.Get(binding,64);if(current is not null)Assert.Equal(png,current);await Task.Yield();}await writes;
        Assert.Equal(png,area.Store.Get(binding,256));
    }
    [Fact]
    public void UnknownSchemaAndInvalidPngFailClosedWithoutRepairingTheDatabase()
    {
        using var area=new Area();var store=area.Store;var binding=area.Binding(store.Configure(area.Root));
        Assert.Throws<IOException>(()=>store.Publish(binding,new Dictionary<int,byte[]>{{64,new byte[100]}}));
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.Database,Pooling=false}.ToString())){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version=99";cmd.ExecuteNonQuery();}
        Assert.Null(store.Root(binding.Root.MountId));Assert.Null(store.Get(binding,64));Assert.Throws<IOException>(()=>store.Configure(area.Root));
    }
}
