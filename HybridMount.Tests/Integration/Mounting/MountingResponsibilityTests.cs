using HybridMount.Tests.Fixtures.Protocols;
using HybridMount.Commons.Mounting;
using HybridMount.Commons.Settings;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Windows.CloudFiles;
using HybridMount.Models.Mounting;
using HybridMount.Tests.Integration.Mounting;
using HybridMount.Tests.Integration.Protocols;
using Microsoft.Win32;

namespace HybridMount.Tests.Integration.Mounting;

/// <summary>公共规则无系统交互、存储和原生提交通过契约装配；回归仍使用隔离根。</summary>
public sealed class MountingResponsibilityTests
{
    private static string RepositoryRoot()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"Directory.Build.props"))&&Directory.Exists(Path.Combine(directory.FullName,"HybridMount","Services","Mounting")))return directory.FullName;
        throw new IOException("结构检查需要源码仓库。");
    }

    [Theory]
    [InlineData(false,0u,false)][InlineData(false,1u,true)][InlineData(true,1u,false)]
    public void Content_rules_distinguish_metadata_from_actual_content(bool directory,uint reason,bool expected)
    {
        var before=new LocalFileStamp("identity",3,50,100,100,directory);var after=before with{Usn=4,ChangeTime=101,Reasons=reason};
        Assert.Equal(expected,UploadRules.ContentChanged(before.Key,after));
        Assert.True(UploadRules.ContentChanged(before.Key,after with{Identity="new identity"}));
    }
    [Fact]
    public async Task Injected_definition_store_does_not_fall_back_or_rewrite_after_read_failure()
    {
        await using var fixture=new DavFixture();var registry=@"Software\HybridMount.NET.Tests\Definitions-"+Guid.NewGuid().ToString("N");
        try
        {
            var store=new DefinitionStore{ReadFailure=true};var directory=Path.Combine(fixture.Area,"definitions");
            Assert.Throws<IOException>(()=>new MountRepository(directory,registry,store));
            Assert.False(Directory.Exists(directory));Assert.Equal(0,store.Writes);
            store.ReadFailure=false;var repository=new MountRepository(directory,registry,store);
            var definition=new MountDefinition(Guid.NewGuid(),"原定义",fixture.Area,Source:new(FileSourceKind.WebDav,"https://fixture.invalid/"));
            repository.SaveDefinition(definition);Assert.Equal(definition,Assert.Single(repository.LoadDefinitions()));
            store.SaveFailure=true;Assert.Throws<IOException>(()=>repository.SaveDefinition(definition with{Name="未保存"}));
            Assert.Equal(definition,Assert.Single(repository.LoadDefinitions()));Assert.Equal(1,store.Writes);
        }
        finally{Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    private sealed class DefinitionStore : ILocalDefinitionTestStore
    {
        public bool ReadFailure,SaveFailure;public int Writes;private MountDefinition? _definition;
        public long CacheLimitBytes{get;set;}=1234;
        public IReadOnlyList<MountDefinition> LoadDefinitions()=>ReadFailure?throw new IOException("test read failure"):_definition is null?[]:[_definition];
        public void SaveDefinition(MountDefinition definition){if(SaveFailure)throw new IOException("test save failure");_definition=definition;Writes++;}
        public void DeleteDefinition(Guid id){if(_definition?.Id==id)_definition=null;}
    }
    private interface ILocalDefinitionTestStore : HybridMount.Commons.Settings.IMountDefinitionStore { }
    private sealed class RecordingCommitter : HybridMount.Commons.Mounting.ILocalUploadCommitter
    {
        public int Calls;
        public LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared)
        {Interlocked.Increment(ref Calls);return CloudUploadState.Commit(mount,entry,prepared);}
    }
    [Fact(Timeout=45000)]
    public async Task Upload_manager_uses_injected_native_commit_boundary()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.DisposeAsync();var committer=new RecordingCommitter();
        test.Manager=new(test.Repository,test.Logger,uploadCommitter:committer);await test.Manager.ConnectAsync(test.Id);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"commit-boundary.txt"),"verified snapshot");await test.Wait("commit-boundary.txt");
        Assert.True(committer.Calls>0);Assert.Equal(1u,CloudFilesNative.Inspect(Path.Combine(test.Local,"commit-boundary.txt"))!.Value.Info.InSyncState);
    }
}
