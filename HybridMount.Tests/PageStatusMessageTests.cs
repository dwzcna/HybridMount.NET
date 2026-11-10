using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>可关闭反馈的发布、静默更新与释放屏障，不依赖WinUI或正式服务。</summary>
public sealed class PageStatusMessageTests
{
    [Fact]
    public void SameOperationReopensButPassiveUpdatesAndNavigationDoNot()
    {
        using var message=new PageStatusMessage();
        message.Update("");Assert.False(message.IsOpen);
        message.Update("首次读取失败");Assert.True(message.IsOpen);
        message.IsOpen=false;message.Update("首次读取失败");Assert.False(message.IsOpen);
        message.Update("已显示 2 条");Assert.False(message.IsOpen);Assert.Equal("已显示 2 条",message.Text);
        message.Show("已显示 2 条");Assert.True(message.IsOpen);
        message.IsOpen=false;Assert.Equal("已显示 2 条",message.Text);
        message.Show("已显示 2 条");Assert.True(message.IsOpen);
        message.Show("");Assert.False(message.IsOpen);
    }
    [Fact]
    public void ChannelsAreIndependentAndDisposalRejectsLateResults()
    {
        using var appearance=new PageStatusMessage();using var cache=new PageStatusMessage();
        appearance.Show("已保存");cache.Show("已保存");appearance.IsOpen=false;
        Assert.True(cache.IsOpen);Assert.False(appearance.IsOpen);
        var calls=0;appearance.PropertyChanged+=(_,_)=>calls++;appearance.Dispose();
        appearance.Show("迟到");appearance.Update("迟到刷新");appearance.IsOpen=true;
        Assert.Equal(0,calls);Assert.False(appearance.IsOpen);Assert.Equal("已保存",appearance.Text);
    }
}
