using System.Diagnostics;
using Xunit.Abstractions;
namespace HybridMount.Tests;

/// <summary>隔离数据库验证一次读取与有界解析，不启动正式挂载或远端请求。</summary>
public sealed class TaskSnapshotCostTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1000)][InlineData(10000)]
    public void SnapshotReadHasFixedQueryCountAndIndexesAncestors(int count)
    {
        using var scratch=new Scratch();var repo=new MountRepository(Path.Combine(scratch.Path,"data"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));var mount=Guid.NewGuid();
        foreach(var batch in Enumerable.Range(0,count).Chunk(256))
        {
            var items=batch.Select(i=>new RemotePendingChange("event-"+i,mount,"scope","dir/file-"+i,new("remote-"+i,"dir/file-"+i,false,10,DateTimeOffset.UnixEpoch,"v1"))).ToArray();
            repo.CommitDiscoveryBatch(mount,[],items,items.Select(x=>new MountOperationSnapshot(x.Id,"remoteUpdate","Queued",0,1,[],null,mount,x.Path)).ToArray());
        }
        var parent=new RemotePendingChange("parent",mount,"scope","dir",new("dir","dir",true,0,DateTimeOffset.UnixEpoch,"v1"),State:"Canceled");repo.SaveRemotePending(parent);
        var samples=new List<double>();TaskStateSnapshot? snapshot=null;
        for(var i=0;i<103;i++){var started=Stopwatch.GetTimestamp();snapshot=repo.ReadTaskState();var elapsed=Stopwatch.GetElapsedTime(started).TotalMilliseconds;if(i>=3)samples.Add(elapsed);}
        Assert.NotNull(snapshot);Assert.Equal(count+1,snapshot.Remote.Count);Assert.Equal(count,snapshot.History.Count);Assert.Equal(5,repo.LastTaskRead.Queries);
        Assert.Equal("parent",snapshot.Blocker(mount,"dir/file-0","event-0")!.Id);Assert.Equal("Canceled",repo.FindRemotePending("parent")!.State);
        var sorted=samples.Order().ToArray();var p95=sorted[94];var p99=sorted[98];output.WriteLine($"rows={count};samples=100;queries={repo.LastTaskRead.Queries};P95={p95:F3}ms;P99={p99:F3}ms");
#if !DEBUG
        Assert.True(p95<=(count==1000?150:750),$"P95={p95:F3}ms");
#endif
    }
    [Fact]
    public void DependencyQueriesKeepCrossScopeDirectoryAndSiblingBoundaries()
    {
        using var scratch=new Scratch();var repo=new MountRepository(Path.Combine(scratch.Path,"data"));var mount=Guid.NewGuid();
        var child=new RemotePendingChange("child",mount,"new","A/child",new("child","A/child",false,1,DateTimeOffset.UnixEpoch,"v"));
        var parent=new RemotePendingChange("parent",mount,"old","a",new("parent","a",true,0,DateTimeOffset.UnixEpoch,"v"),State:"Committing");repo.SaveRemotePending(parent);repo.SaveRemotePending(child);
        Assert.False(repo.RemoteDependenciesReady(child));repo.SaveRemotePending(parent with{State="Completed"});Assert.True(repo.RemoteDependenciesReady(child));
        var deletion=new RemotePendingChange("delete",mount,"new","A",null);repo.SaveRemotePending(deletion);Assert.False(repo.RemoteDependenciesReady(deletion));
        repo.SaveRemotePending(child with{State="Completed"});repo.SaveRemotePending(child with{Id="sibling",Path="AB/child",State="Pending"});Assert.True(repo.RemoteDependenciesReady(deletion));
    }
    [Fact]
    public async Task ManagedSelectionIsStructuredAndKeepsCandidateProtection()
    {
        using var scratch=new Scratch();var definitions=new Definitions();var local=Path.Combine(scratch.Path,"mount");Directory.CreateDirectory(local);
        var definition=new MountDefinition(Guid.NewGuid(),"managed",local,false);definitions.Items.Add(definition);
        var repo=new MountRepository(Path.Combine(scratch.Path,"data"),definitions:definitions);await using var logger=new LoggerService(Path.Combine(scratch.Path,"logs.db"));
        await using var manager=new MountManager(repo,logger);
        foreach(var path in new[]{local,Path.Combine(local,"child"),scratch.Path})
        {
            var result=await manager.InspectOrphanedMountSelectionAsync(path);Assert.Null(result.Candidate);Assert.Equal(definition,result.Managed);
            Assert.Contains(local,result.Message);Assert.Equal(ApplicationPaths.DeploymentScope,result.OwnerMode);
        }
        Assert.Single(definitions.Items);Assert.Empty(repo.LoadOperations());
    }
    private sealed class Definitions:IMountDefinitionStore
    {
        internal readonly List<MountDefinition> Items=[];
        public long CacheLimitBytes {get;set;}
        public IReadOnlyList<MountDefinition> LoadDefinitions()=>Items.ToArray();
        public void SaveDefinition(MountDefinition definition){Items.RemoveAll(x=>x.Id==definition.Id);Items.Add(definition);}
        public void DeleteDefinition(Guid id)=>Items.RemoveAll(x=>x.Id==id);
    }
    private sealed class Scratch:IDisposable
    {
        internal string Path {get;}=Directory.CreateTempSubdirectory("HybridMount.TaskSnapshot.").FullName;
        public void Dispose()
        {
            var expected=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HybridMount.TaskSnapshot.");
            if(!System.IO.Path.GetFullPath(Path).StartsWith(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("测试根越界。");
            Directory.Delete(Path,true);
        }
    }

}
