using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit.Abstractions;
namespace HybridMount.Tests;

/// <summary>本地路径屏障与任务快照一致；S3原始键保持精确，不发送远端请求。</summary>
public sealed class RemotePathBarrierTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Folder","fOLDER")][InlineData("Équipe","éQUIPE")][InlineData("Σ","σ")][InlineData("Ж","ж")]
    public void AncestorBarriersUseOrdinalIgnoreCaseInAllReaders(string stored,string queried)
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();var parent=Pending(mount,"parent",stored,true) with{State="Committing",Scope="retired"};repo.SaveRemotePending(parent);
        var child=Pending(mount,"child",queried+"/file",false);
        Assert.True(repo.HasPendingRemote(mount,child.Path));Assert.False(repo.RemoteDependenciesReady(child));Assert.Equal("parent",Assert.Single(repo.RemoteAncestors(mount,child.Path)).Id);Assert.Equal("parent",repo.ReadTaskState().Blocker(mount,child.Path,"child")!.Id);
        Assert.False(repo.HasPendingRemote(mount,queried+"-sibling/file"));Assert.False(repo.HasPendingRemote(Guid.NewGuid(),child.Path));
        repo.SaveRemotePending(parent with{State="Completed"});Assert.False(repo.HasPendingRemote(mount,child.Path));Assert.True(repo.RemoteDependenciesReady(child));
    }
    [Fact]
    public void RootAndLiteralWildcardPathsKeepBoundaryAndDeletionDependencies()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();var parent=Pending(mount,"p","A%_",true);repo.SaveRemotePending(parent);
        Assert.True(repo.HasPendingRemote(mount,"a%_/leaf"));Assert.False(repo.HasPendingRemote(mount,"ABC/leaf"));Assert.False(repo.HasPendingRemote(mount,"A%_sibling/leaf"));
        var child=Pending(mount,"c","a%_/子目录/file",false);repo.SaveRemotePending(child);
        Assert.False(repo.RemoteDependenciesReady(new("delete",mount,"scope","A%_",null)));Assert.False(repo.RemoteDependenciesReady(new("delete-root",mount,"scope","",null)));
        repo.SaveRemotePending(parent with{State="Superseded"});repo.SaveRemotePending(child with{State="Completed"});Assert.True(repo.RemoteDependenciesReady(new("delete-root",mount,"scope","",null)));
        var root=Pending(mount,"root","",true);repo.SaveRemotePending(root);Assert.True(repo.HasPendingRemote(mount,"anything/file"));Assert.False(repo.RemoteDependenciesReady(Pending(mount,"new","anything",false)));
    }
    [Theory]
    [InlineData("Canceled",true,false)][InlineData("Canceled",false,true)][InlineData("Completed",false,false)][InlineData("Superseded",false,false)][InlineData("Failed",true,true)]
    public void TerminalAndMetadataCancellationAgreeAcrossBarriers(string state,bool metadataOnly,bool blocks)
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();repo.SaveRemotePending(Pending(mount,"parent","É",true) with{State=state,MetadataOnly=metadataOnly});var child=Pending(mount,"child","é/file",false);
        Assert.Equal(blocks,repo.HasPendingRemote(mount,child.Path));Assert.Equal(!blocks,repo.RemoteDependenciesReady(child));Assert.Equal(blocks,repo.RemoteAncestors(mount,child.Path).Count>0);Assert.Equal(blocks,repo.ReadTaskState().Blocker(mount,child.Path,"child") is not null);
    }
    [Fact]
    public void RawS3KeysStaySensitiveWhileMaterializedLocalPathsAreConservative()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();
        repo.PersistSqsMessage("queue","message",[new("hint",mount,"s3","Object","bucket","01","ObjectCreated:Put")]);
        Assert.True(repo.HasPendingRemote(mount,"Object"));Assert.False(repo.HasPendingRemote(mount,"object"));
        repo.SaveRemotePending(Pending(mount,"mapped","Object",false));Assert.True(repo.HasPendingRemote(mount,"object"));
        Assert.Equal("Object",Assert.Single(repo.S3EventHints(mount,"s3")).Path);
    }
    [Fact]
    public void OrdinalIndexSurvivesReadOnlyPreflightAndIsUsedForPointAncestors()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();repo.SaveRemotePending(Pending(mount,"parent","É",true));
        Assert.Equal("Current",MountIndexFormat.Inspect(area.Path).State);var reopened=new MountRepository(area.Path);Assert.True(reopened.HasPendingRemote(mount,"é/file"));
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(area.Path,"mounts.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();MountDatabase.ConfigurePathComparison(db);
        using var query=db.CreateCommand();query.CommandText="EXPLAIN QUERY PLAN SELECT 1 FROM remote_pending WHERE mount=$m AND path COLLATE HM_PATH IN (SELECT value FROM json_each($paths))";query.Parameters.AddWithValue("$m",mount.ToString("N"));query.Parameters.AddWithValue("$paths",JsonSerializer.Serialize(new[]{"é","é/file",""}));
        using var rows=query.ExecuteReader();var plans=new List<string>();while(rows.Read())plans.Add(rows.GetString(3));var plan=string.Join(";",plans);output.WriteLine(plan);Assert.Contains("remote_pending_path_ordinal",plan);Assert.DoesNotContain("SCAN remote_pending",plan);
    }
    [Fact]
    public void RelatedMappedQueriesCoverUnicodeAndTheWholeRootWithoutSiblings()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();
        var parent=new MountEntry("parent","É","É","v1",true);repo.SaveEntry(mount,parent);repo.SaveEntry(mount,new("child","É/file","É/file","v1",false));repo.SaveEntry(mount,new("sibling","Éx/file","Éx/file","v1",false));
        Assert.Equal(new[]{"child","parent"},repo.EntriesInLocalSubtree(mount,"é").Select(x=>x.Id).Order());Assert.Equal(3,repo.EntriesInLocalSubtree(mount,"").Count);
        Assert.True(repo.SetRemotePresence(mount,parent,RemotePresence.Deleted));Assert.Equal("parent",repo.DeletedSourceAncestor(mount,"é/file")!.Id);
        repo.SaveUpload(new("upload",mount,"É/file","É/file",false,"stamp",1,"Queued",DateTimeOffset.UnixEpoch));repo.SaveUpload(new("other",mount,"Éx/file","Éx/file",false,"stamp",1,"Queued",DateTimeOffset.UnixEpoch));
        Assert.Equal("upload",Assert.Single(repo.UploadsRelated(mount,"é")).Id);Assert.Equal(2,repo.UploadsRelated(mount,"").Count);
    }
    private static RemotePendingChange Pending(Guid mount,string id,string path,bool directory)=>new(id,mount,"scope",path,new(id,path,directory,1,DateTimeOffset.UnixEpoch,"v1"));
    private sealed class Area:IDisposable
    {
        internal string Path{get;}=Directory.CreateTempSubdirectory("HybridMount.PathBarrier.").FullName;
        public void Dispose()=>Directory.Delete(Path,true);
    }
}
