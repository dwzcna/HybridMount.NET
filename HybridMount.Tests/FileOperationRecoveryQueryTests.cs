using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit.Abstractions;
namespace HybridMount.Tests;

/// <summary>每文件恢复验证主键定位及身份复用；不创建挂载，不执行云请求。</summary>
public sealed class FileOperationRecoveryQueryTests(ITestOutputHelper output)
{
    [Fact]
    public void LargeHistoryUsesPrimaryKeyWithoutDeserializingUnrelatedRows()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();var entry=new MountEntry("entry","file","file","v1",false);
        var expected=Snapshot(mount,entry);repo.SaveOperation(expected);
        using var db=Open(area.Path);using(var tx=db.BeginTransaction())
        {
            using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="INSERT INTO operations VALUES($id,$j)";
            command.Parameters.AddWithValue("$id","");command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(new{MountId=mount,OperationEntryId="unrelated",State=42}));
            for(var index=0;index<10000;index++){command.Parameters["$id"].Value="unrelated-"+index;command.ExecuteNonQuery();}tx.Commit();
        }
        for(var index=0;index<256;index++)Assert.Equal(JsonSerializer.Serialize(expected),JsonSerializer.Serialize(repo.FindRecoverableFileOperation(expected.Id,mount,entry,"freeSpace")));
        Assert.Null(repo.FindRecoverableFileOperation("missing",mount,entry,"freeSpace"));
        string Plan(string sql)
        {
            using var command=db.CreateCommand();command.CommandText="EXPLAIN QUERY PLAN "+sql;command.Parameters.AddWithValue("$id",expected.Id);command.Parameters.AddWithValue("$mount",mount.ToString());
            using var rows=command.ExecuteReader();var details=new List<string>();while(rows.Read())details.Add(rows.GetString(3));return string.Join(";",details);
        }
        var point=Plan("SELECT json FROM operations WHERE id=$id");var batch=Plan("SELECT rowid,json FROM operations WHERE json_extract(json,'$.MountId')=$mount AND json_extract(json,'$.OperationEntryId') IS NOT NULL");
        output.WriteLine("history=10001; point="+point+"; batch="+batch);Assert.Contains("SEARCH operations",point);Assert.Contains("(id=?)",point);Assert.Contains("operations_file_mount",batch);Assert.DoesNotContain("SCAN operations",point+batch);
    }
    [Theory]
    [InlineData("Running")][InlineData("Deferred")][InlineData("Interrupted")]
    public void MatchingRecoveryPreservesAttemptAndFailureEvidence(string state)
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();var entry=new MountEntry("entry","file","file","v1",false);var expected=Snapshot(mount,entry) with{State=state};repo.SaveOperation(expected);
        Assert.Equal(JsonSerializer.Serialize(expected),JsonSerializer.Serialize(repo.FindRecoverableFileOperation(expected.Id,mount,entry,"freeSpace")));
    }
    [Theory]
    [InlineData("mount")][InlineData("entry")][InlineData("operation")]
    [InlineData("Completed")][InlineData("Canceled")][InlineData("Failed")][InlineData("Superseded")]
    public void ChangedOwnerContentOrTerminalStateCannotReuseAttempt(string field)
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();var entry=new MountEntry("entry","file","file","v1",false);var row=Snapshot(mount,entry);
        row=field switch{"mount"=>row with{MountId=Guid.NewGuid()},"entry"=>row with{OperationEntryId="other"},"operation"=>row with{Operation="keepOnDevice"},_=>row with{State=field}};
        repo.SaveOperation(row);Assert.Null(repo.FindRecoverableFileOperation(row.Id,mount,entry,"freeSpace"));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void RecoveryKeepsOldContentEvidenceForVerifierInsteadOfCreatingNewAttempt(bool version)
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();var entry=new MountEntry("entry","file","file","v1",false);var old=Snapshot(mount,entry);
        old=version?old with{OperationVersion="old-version"}:old with{OperationFileId="old-file-id"};repo.SaveOperation(old);
        var recovered=repo.FindRecoverableFileOperation(old.Id,mount,entry,"freeSpace");Assert.NotNull(recovered);Assert.Equal(old.AttemptId,recovered.AttemptId);Assert.Equal(old.OperationVersion,recovered.OperationVersion);Assert.Equal(old.OperationFileId,recovered.OperationFileId);
    }
    private static MountOperationSnapshot Snapshot(Guid mount,MountEntry entry)=>new("old-task","freeSpace","Deferred",0,1,[],"locked",mount,entry.LocalPath)
    {OperationEntryId=entry.Id,OperationVersion=entry.Version,OperationFileId="file-id",AttemptId="same-attempt",FailureStage="release",FailureHResult=unchecked((int)0x80070020)};
    private static SqliteConnection Open(string path){var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(path,"mounts.db"),Pooling=false}.ToString());db.Open();return db;}
    private sealed class Area:IDisposable
    {
        internal string Path{get;}=Directory.CreateTempSubdirectory("HybridMount.OperationRecovery.").FullName;
        public void Dispose()=>Directory.Delete(Path,true);
    }
}
