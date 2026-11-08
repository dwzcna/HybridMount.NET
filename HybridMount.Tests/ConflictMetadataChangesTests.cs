using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>元数据授权必须有同路径完整戳的连续证明；缺边、外部写入和身份替换不会被宽泛放行。</summary>
public sealed class ConflictMetadataChangesTests
{
    [Fact]
    public void OnlyExactRecordedChainCanRebaseAComparison()
    {
        var changes=new ConflictMetadataChanges();var first=new LocalFileStamp("file",1,12,50,60,false);var second=first with{Usn=2,ChangeTime=61};var third=second with{Usn=3,ChangeTime=62};
        changes.Record("a.txt",first,second);changes.Record("a.txt",second,third);
        Assert.True(changes.Allows("a.txt",first.Key,third.Key));Assert.False(changes.Allows("b.txt",first.Key,third.Key));Assert.False(changes.Allows("a.txt",first.Key,(third with{Usn=4}).Key));
        changes.Record("a.txt",third,third with{Identity="replacement",Usn=5});Assert.False(changes.Allows("a.txt",third.Key,(third with{Identity="replacement",Usn=5}).Key));
    }
    [Fact]
    public void MetadataProofsAreBoundedAndCannotAcceptContentChanges()
    {
        var changes=new ConflictMetadataChanges();var before=new LocalFileStamp("file",1,12,50,60,false);var after=before with{Usn=2,ChangeTime=61};
        changes.Record("old",before,after);for(var i=0;i<512;i++)changes.Record("other"+i,before,after);
        Assert.False(changes.Allows("old",before.Key,after.Key));changes.Record("data",before,after with{Length=13});Assert.False(changes.Allows("data",before.Key,(after with{Length=13}).Key));
    }
}
