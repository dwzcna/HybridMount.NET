using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>挂载卡片容量与连接动作边界；不将未知值冒充零，不修改服务或配置。</summary>
public sealed class MountRowTests
{
    private static MountSnapshot Snapshot()=>new(new(Guid.NewGuid(),"挂载",@"D:\挂载",Source:new(FileSourceKind.WebDav,"https://example.test/")),"未连接","");
    [Fact]
    public void Capacity_is_independent_of_selection_and_unknown_values_stay_unknown()
    {
        var snapshot=Snapshot();var item=new MountListItem(snapshot);Assert.Equal("已用：未知",item.UsedText);Assert.Equal("总空间：不限",item.TotalText);Assert.Equal("—",item.PercentageText);
        snapshot=snapshot with{Capacity=new(new("服务器",75_000,25_000))};item.Update(snapshot,false,false,false,false,false);
        Assert.Equal("已用：25.00 kB",item.UsedText);Assert.Equal("总空间：100.00 kB",item.TotalText);Assert.Equal("25.0%",item.PercentageText);
        item.Update(snapshot,true,false,false,false,false);Assert.True(item.IsSelected);Assert.Equal("25.0%",item.PercentageText);
        snapshot=snapshot with{Capacity=new(new("手动",0,120,100))};item.Update(snapshot,true,false,false,false,false);Assert.Equal("120.0%",item.PercentageText);
    }
    [Fact]
    public void Connection_button_changes_action_and_cancel_remains_available_while_connecting()
    {
        var snapshot=Snapshot();var item=new MountListItem(snapshot);Assert.Equal("连接",item.ConnectionText);Assert.True(item.CanChangeConnection);
        snapshot=snapshot with{IsConnecting=true};item.Update(snapshot,true,true,false,false,false);Assert.Equal("取消连接",item.ConnectionText);Assert.True(item.CanChangeConnection);
        item.Update(snapshot,true,true,false,false,true);Assert.False(item.CanChangeConnection);
        snapshot=snapshot with{IsConnecting=false,IsConnected=true,Status="已连接"};item.Update(snapshot,true,false,false,false,false);Assert.Equal("断开",item.ConnectionText);Assert.True(item.CanRun);
        snapshot=snapshot with{IsConnected=false,Status="异常"};item.Update(snapshot,true,false,false,false,false);Assert.Equal("重试连接",item.ConnectionText);
        item.Update(snapshot,true,false,false,true,false);Assert.False(item.CanEdit);Assert.False(item.CanInspect);
    }
    [Theory]
    [InlineData(12345,"12.35 kB")][InlineData(12345678,"12.35 MB")][InlineData(12345678900,"12.35 GB")]
    public void Capacity_uses_fixed_two_decimal_places(long bytes,string text)=>Assert.Equal(text,MountListItem.FormatBytes(bytes));
}
