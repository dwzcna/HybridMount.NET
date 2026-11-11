using System.Collections.ObjectModel;
using System.Collections.Specialized;
using HybridMount.ViewModels;
namespace HybridMount.Tests;
/// <summary>生产录屏所示同ID更新的隔离回归；列表事件和对象身份是实际界面容器重建的边界。</summary>
public sealed class TaskListItemTests
{
    private static MountOperationSnapshot Item(string id="upload",long bytes=0,string state="Sending")=>new(id,"upload",state,0,1,[],null,ItemPath:"folder/file.bin",BytesTransferred:bytes,TotalBytes:1000,IsDirectory:false);
    [Fact]public void ThousandProgressChangesKeepRowAndNeverReplaceCollectionItem()
    {
        var rows=new ObservableCollection<TaskListItem>();TaskListItem.Reconcile(rows,[Item()]);var original=rows.Single();var collection=0;rows.CollectionChanged+=(_,_)=>collection++;var notices=new List<string?>();original.PropertyChanged+=(_,args)=>notices.Add(args.PropertyName);
        for(var i=1;i<=1000;i++){Assert.False(TaskListItem.Reconcile(rows,[Item(bytes:i)]));Assert.Same(original,rows.Single());Assert.Equal(i/10.0,original.ProgressPercent);}
        Assert.Equal(0,collection);Assert.DoesNotContain(nameof(TaskListItem.CardPath),notices);Assert.Contains(nameof(TaskListItem.ProgressPercent),notices);
    }
    [Fact]public void EqualResultValuesInNewListDoNotNotifyOrReplace()
    {
        var first=Item() with{Results=[new("folder/file.bin","Running","")]};var rows=new ObservableCollection<TaskListItem>();TaskListItem.Reconcile(rows,[first]);var count=0;rows.CollectionChanged+=(_,_)=>count++;rows[0].PropertyChanged+=(_,_)=>count++;
        Assert.False(TaskListItem.Reconcile(rows,[first with{Results=[new("folder/file.bin","Running","")]}]));Assert.Equal(0,count);
    }
    [Fact]public void GroupOrderUsesMovesAndStateStyleChangesInExistingRow()
    {
        var rows=new ObservableCollection<TaskListItem>();TaskListItem.Reconcile(rows,[Item("a"),Item("b")]);var a=rows[0];var b=rows[1];var replaced=false;rows.CollectionChanged+=(_,args)=>replaced|=args.Action==NotifyCollectionChangedAction.Replace;
        Assert.True(TaskListItem.Reconcile(rows,[Item("b",state:"Conflict"),Item("a",state:"Failed")]));Assert.Same(b,rows[0]);Assert.Same(a,rows[1]);Assert.Equal(TaskIssueKind.Conflict,b.IssueKind);Assert.Equal(TaskIssueKind.Error,a.IssueKind);Assert.False(replaced);
        Assert.True(TaskListItem.Reconcile(rows,[Item("a",state:"Completed")]));Assert.Same(a,rows.Single());Assert.Equal(TaskIssueKind.Normal,a.IssueKind);
    }
}
