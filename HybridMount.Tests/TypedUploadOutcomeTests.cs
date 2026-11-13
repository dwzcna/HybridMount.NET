using System.Text;
namespace HybridMount.Tests;

/// <summary>迟到目录创建回执在类型替换后仍被核验；缺失结果不得终结原尝试，全部使用回环DAV。</summary>
public sealed class TypedUploadOutcomeTests
{
    [Fact(Timeout=60000)]
    public async Task MissingTypedMutationStaysUnknownThenRetainsLateDirectoryReceipt()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"late-typed");await File.WriteAllTextAsync(path,"replacement file");var stamp=LocalFileState.Read(path);
        var item=new LocalUpload(Guid.NewGuid().ToString("N"),test.Id,"late-typed","late-typed",true,stamp.Key,1,"OutcomeUnknown",DateTimeOffset.UtcNow,SnapshotStamp:"original-directory",SnapshotGeneration:1,Error:UploadRules.TypeConflictMessage);test.Repository.SaveUpload(item);
        await test.Manager.ReconcileUploadForTests(test.Id,item.Id);var missing=test.Repository.Upload(item.Id)!;Assert.Equal("OutcomeUnknown",missing.State);Assert.Equal(UploadRules.TypeConflictMessage,missing.Error);Assert.Null(missing.Receipt);
        test.Dav.Objects["late-typed"]=new([],"\"late-directory\"",true);await test.Manager.ReconcileUploadForTests(test.Id,item.Id);var confirmed=test.Repository.Upload(item.Id)!;Assert.Equal("Conflict",confirmed.State);Assert.NotNull(confirmed.Receipt);Assert.True(confirmed.Receipt.IsDirectory);Assert.Equal("original-directory",confirmed.SnapshotStamp);Assert.Equal("replacement file",await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(test.Dav.Server.Requests,request=>request.Target.Contains("late-typed",StringComparison.Ordinal)&&request.Method is "PUT" or "MKCOL" or "DELETE" or "MOVE");
    }
}
