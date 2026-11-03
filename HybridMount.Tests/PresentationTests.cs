using System.Collections.ObjectModel;
using System.Globalization;
using HybridMount.Models.Mounting;

namespace HybridMount.Tests;

/// <summary>验证十进制单位、目录计数和按挂载的操作许可；不以模型测试代替真实 WinUI 交互。</summary>
public sealed class PresentationTests
{
    [Theory]
    [InlineData(0,"0 字节")][InlineData(999,"999 字节")][InlineData(1000,"1 kB")]
    [InlineData(1500,"1.5 kB")][InlineData(999999,"1 MB")][InlineData(1234567,"1.23 MB")]
    [InlineData(1000000000,"1 GB")][InlineData(1000000000000,"1 TB")]
    [InlineData(1000000000000000,"1 PB")][InlineData(long.MaxValue,"9.22 EB")]
    public void Decimal_units_preserve_raw_value(long bytes,string expected)
    {
        var previous=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;Assert.Equal(expected,TaskByteFormat.Format(bytes));}
        finally{CultureInfo.CurrentCulture=previous;}
    }
    [Fact]
    public void File_progress_has_exact_tooltip_and_directory_uses_items()
    {
        var file=new MountOperationSnapshot("x","upload","Sending",0,1,[],null,BytesTransferred:1001,TotalBytes:2000,IsDirectory:false);
        Assert.Contains("kB",file.ProgressText);Assert.DoesNotContain("kB",file.ProgressDetail);Assert.Equal(50.05,file.ProgressPercent,5);
        var directory=file with{IsDirectory=true,Completed=1,Total=2};Assert.Contains("1 / 2 项",directory.ProgressText);
        Assert.Contains("kB",(file with{Operation="remoteUpdate"}).ProgressText);
    }
    private static MountSnapshot Mount(bool connected=false,bool connecting=false,bool pending=false)=>new(
        new(Guid.NewGuid(),"挂载",@"D:\test",Source:new(FileSourceKind.WebDav,"https://example.test/dav/"),RemovalPending:pending),
        connecting?"连接中":connected?"已连接":"未连接","",IsConnected:connected,IsConnecting:connecting);
    [Fact]
    public void Connection_cancel_is_available_while_same_mount_is_busy()
    {
        var state=MountActionState.From(Mount(connecting:true),busy:true);
        Assert.True(state.CanCancelConnection);Assert.False(state.CanConnect);Assert.False(state.CanEdit);
        Assert.True(MountActionState.From(Mount()).CanConnect);
        Assert.True(MountActionState.From(Mount(connected:true)).CanRunConnected);
        Assert.False(MountActionState.From(Mount(connected:true),busy:true).CanRunConnected);
    }
    [Fact]
    public void Unselected_stopping_and_pending_removal_are_restricted()
    {
        Assert.False(MountActionState.From(null).CanOpen);Assert.False(MountActionState.From(Mount(),stopping:true).CanRemove);
        var pending=Mount(pending:true);var state=MountActionState.From(pending);
        Assert.True(state.CanRemove);Assert.False(state.CanConnect);Assert.False(state.CanRunConnected);Assert.Contains("继续",state.RemoveText);
        Assert.False(MountActionState.From(pending with{ActiveOperation="remove"}).CanRemove);
    }
    [Fact]
    public void Snapshot_reconciliation_does_not_reset_unchanged_rows()
    {
        var a=Mount();var b=Mount();var list=new ObservableCollection<MountSnapshot>{a,b};var resets=0;
        list.CollectionChanged+=(_,args)=>{if(args.Action==System.Collections.Specialized.NotifyCollectionChangedAction.Reset)resets++;};
        SnapshotCollections.Reconcile(list,new[]{a,b with{Status="已连接"}},x=>x.Definition.Id);
        Assert.Same(a,list[0]);Assert.Equal("已连接",list[1].Status);Assert.Equal(0,resets);
        SnapshotCollections.Reconcile(list,new[]{b,a},x=>x.Definition.Id);Assert.Equal(b.Definition.Id,list[0].Definition.Id);Assert.Equal(0,resets);
    }
}
