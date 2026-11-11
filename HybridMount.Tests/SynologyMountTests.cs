using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting.Synology;

namespace HybridMount.Tests;

/// <summary>真实 CFAPI 与群晖响应器联合验证；每例建立独立 NTFS 根，结束明确注销并核对。</summary>
[Collection("Native IPC")]
public sealed class SynologyMountTests
{
    [Fact(Timeout=90000)]
    public async Task PinDownloadsWithoutOpeningThenReleaseDehydratesSamePlaceholder()
    {
        await using var test=await Fixture.Create();var path=Path.Combine(test.Local,"file.bin");var initial=CloudFilesNative.Inspect(path)!.Value.Identity;
        Assert.False(CloudFilesNative.HasFullContent(path,8));
        var pin=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));Assert.True(pin.State=="Completed",JsonSerializer.Serialize(pin));
        Assert.True(CloudFilesNative.HasFullContent(path,8));Assert.Equal(test.Server.Files["/share/file.bin"],await File.ReadAllBytesAsync(path));
        Assert.Equal(initial,CloudFilesNative.Inspect(path)!.Value.Identity);Assert.Equal(1,test.Server.Downloads);
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[path]))).State);
        Assert.False(CloudFilesNative.HasFullContent(path,8));Assert.Equal(initial,CloudFilesNative.Inspect(path)!.Value.Identity);
    }
    [Fact(Timeout=90000)]
    public async Task ExistingFileConflictDoesNotBlockUnrelatedNewUploadOrChangeRemoteContent()
    {
        await using var test=await Fixture.Create();var path=Path.Combine(test.Local,"file.bin");
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));
        await File.WriteAllTextAsync(path,"local edit");await File.WriteAllBytesAsync(Path.Combine(test.Local,"new.bin"),[9,8,7]);
        await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        await test.Wait("file.bin","Conflict");await test.Wait("new.bin","Completed");
        Assert.Equal("abcdefgh",Encoding.UTF8.GetString(test.Server.Files["/share/file.bin"]));Assert.Equal(new byte[]{9,8,7},test.Server.Files["/share/new.bin"]);
        Assert.DoesNotContain(test.Server.Uploads,x=>x["file"]=="file.bin");
        var task=test.Repository.Uploads(test.Id).First(x=>x.LocalPath=="file.bin"&&x.State=="Conflict");
        var comparison=await test.Manager.CompareConflictAsync(task.Id);Assert.False(comparison.CanUseLocal);Assert.Contains("另存",comparison.LocalRestriction);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ExportChoosesSchemaFromContentsAndImportClearsDeviceTokenWithoutConnecting(bool synology)
    {
        await using var test=await Fixture.Create(connect:false);var source=synology?SynologyTests.Options:new(FileSourceKind.WebDav,"https://example.test/");
        var item=new MountBackupItem("imported","old-machine",source,new("user","password",SessionToken:synology?"device-secret":""));
        var target=Path.Combine(test.Area,"imported");Directory.CreateDirectory(target);
        var imported=await test.Manager.ImportConfigurationsAsync([new(item,target)]);Assert.Equal("Saved",Assert.Single(imported.Results).State);
        var definition=Assert.Single(test.Manager.Mounts).Definition;Assert.Empty(new CredentialStore(test.Repository.DataDirectory).Load(definition.Id).SessionToken);
        var backup=Path.Combine(test.Area,"test.hymcfg");await test.Manager.ExportConfigurationsAsync([definition.Id],"",backup);
        var decoded=MountBackupCodec.Decode(await File.ReadAllBytesAsync(backup),"");Assert.Equal(synology?6:5,decoded.SchemaVersion);
        Assert.Empty(test.Server.Logins);Assert.Empty(test.Server.Uploads);
    }
    private sealed class Definitions:IMountDefinitionStore
    {
        private readonly Dictionary<Guid,MountDefinition> _items=[];
        public long CacheLimitBytes {get;set;}
        public IReadOnlyList<MountDefinition> LoadDefinitions()=>_items.Values.ToArray();
        public void SaveDefinition(MountDefinition definition)=>_items[definition.Id]=definition;
        public void DeleteDefinition(Guid id)=>_items.Remove(id);
    }
    private sealed class Factory(SynologyFakeDsm server):IFileSourceFactory
    {
        public async Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)
        {
            var source=new SynologyFileSource(mount.Source!,new CredentialStore(repository.DataDirectory).Load(mount.Id),new WholeFileCache(Path.Combine(repository.DataDirectory,"RangeCache",mount.Id.ToString("N"))),server,null);
            try
            {
                if(TasksQueueService.CurrentBinding is {} binding)((IMountNetworkSource)source).BindQueue(binding);
                ((IMountSourceRuntime)source).AttachRuntime(mount,repository);await source.ProbeAsync(token);return source;
            }
            catch{await source.DisposeAsync();throw;}
        }
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal string Area {get;}=Path.Combine(Path.GetTempPath(),"HybridMount.SynologyMountTests",Guid.NewGuid().ToString("N"));
        internal string Local=>Path.Combine(Area,"root");
        internal readonly SynologyFakeDsm Server=new();
        internal MountRepository Repository=null!;internal MountManager Manager=null!;private LoggerService _logger=null!;
        internal Guid Id;private MountDefinition? _registered;
        internal static async Task<Fixture> Create(bool connect=true)
        {
            var test=new Fixture();Directory.CreateDirectory(test.Local);test.Repository=new(Path.Combine(test.Area,"data"),definitions:new Definitions());
            test._logger=new(Path.Combine(test.Area,"log.db"));test.Manager=new(test.Repository,test._logger,new Factory(test.Server)){LegacyPreviewsForTests=false};
            try
            {
                if(connect)
                {
                    var mount=await test.Manager.AddAsync("Synology-00957-"+Guid.NewGuid().ToString("N"),test.Local,false,source:SynologyTests.Options,credentials:new("user","password"),autoUpload:false);
                    test.Id=mount.Id;test._registered=mount;await test.Manager.SetSchedulingForTestsAsync(test.Id,false);test.SaveEvidence("registered",true);
                }
                return test;
            }
            catch{await test.DisposeAsync();throw;}
        }
        internal async Task Wait(string path,string state)
        {
            var deadline=DateTimeOffset.UtcNow.AddSeconds(30);
            while(DateTimeOffset.UtcNow<deadline)
            {
                var item=Repository.Uploads(Id).FirstOrDefault(x=>x.LocalPath==path&&x.State==state);if(item is not null)return;
                await Task.Delay(50);
            }
            throw new TimeoutException(JsonSerializer.Serialize(Repository.Uploads(Id)));
        }
        private void SaveEvidence(string phase,bool owned)
        {
            var root=new DirectoryInfo(AppContext.BaseDirectory);while(root.Parent is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;
            var folder=Path.Combine(root.FullName,"artifacts","validation",typeof(SynologyMountTests).Assembly.GetName().Version!.ToString(),"synology-cfapi");Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"cfapi-"+Path.GetFileName(Area)+"-"+phase+".json"),JsonSerializer.Serialize(new{Root=Local,Mount=_registered?.Id,RootId=_registered is null?null:CloudRootRegistration.Id(_registered),Owned=owned,Requests=Server.Requests.Count,Downloads=Server.Downloads}));
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if(Manager is not null)
                {
                    if(_registered is not null)foreach(var mount in Manager.Mounts.ToArray())await WebDavTests.Finished(Manager,Manager.Queue(mount.Definition.Id,"clearAndRemove"));
                    await Manager.DisposeAsync();
                }
                if(_registered is {} registered){var owned=CloudRootRegistration.IsOwnedRoot(registered);SaveEvidence("removed",owned);Assert.False(owned);}
            }
            finally{if(_logger is not null)await _logger.DisposeAsync();if(Directory.Exists(Area))Directory.Delete(Area,true);}
        }
    }
}
