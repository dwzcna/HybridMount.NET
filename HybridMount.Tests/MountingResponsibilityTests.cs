using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.Win32;

namespace HybridMount.Tests;

/// <summary>公共规则无系统交互、存储和原生提交通过契约装配；回归仍使用隔离根。</summary>
public sealed class MountingResponsibilityTests
{
    private static string RepositoryRoot()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"Directory.Build.props"))&&Directory.Exists(Path.Combine(directory.FullName,"HybridMount","Services","Mounting")))return directory.FullName;
        throw new IOException("结构检查需要源码仓库。");
    }
    [Fact]
    public void Shared_models_have_no_service_or_native_dependency_and_service_roots_are_classified()
    {
        var root=RepositoryRoot();var model=Path.Combine(root,"HybridMount","Models","Mounting");
        foreach(var file in Directory.EnumerateFiles(model,"*.cs",SearchOption.TopDirectoryOnly))
        {
            var text=File.ReadAllText(file);Assert.DoesNotContain("HybridMount.Services.",text);Assert.DoesNotContain("DllImport(",text);Assert.DoesNotContain("LibraryImport(",text);
        }
        var services=Path.Combine(root,"HybridMount","Services","Mounting");
        foreach(var file in Directory.EnumerateFiles(services,"*.cs",SearchOption.TopDirectoryOnly))
        {
            var name=Path.GetFileName(file);
            Assert.True(name.StartsWith("MountManager.",StringComparison.Ordinal),name+" 尚未归入实际职责。");
        }
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
            var store=new DefinitionStore{ReadFailure=true};var repository=new MountRepository(Path.Combine(fixture.Area,"definitions"),registry,store);
            Assert.Throws<IOException>(()=>repository.LoadDefinitions());Assert.Equal(0,store.Writes);
            store.ReadFailure=false;var definition=new MountDefinition(Guid.NewGuid(),"原定义",fixture.Area);
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
    private interface ILocalDefinitionTestStore : HybridMount.Commons.IMountDefinitionStore { }
    private sealed class RecordingCommitter : HybridMount.Commons.ILocalUploadCommitter
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
