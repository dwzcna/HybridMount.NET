using HybridMount.Helpers.UI;
namespace HybridMount.Tests;
/// <summary>验证页头空间优先级与扩展滞后；输入为真实控件测量值，不依赖语言固定像素阈值。</summary>
public sealed class HeaderLayoutTests
{
    [Theory]
    [InlineData(440,2,false)][InlineData(450,3,false)][InlineData(630,3,true)][InlineData(680,4,true)]
    public void FirstThreeActionsPrecedeFilterTextAndRemainingActions(double width,int count,bool labels)
    {var result=HeaderLayoutPolicy.Choose(width,144,320,new double[]{70,80,90,100});Assert.Equal(count,result.Count);Assert.Equal(labels,result.ShowLabels);Assert.True(result.RequiredWidth<=width);}
    [Fact]
    public void FewerThanThreeActionsExpandBeforeFilterText()
    {var result=HeaderLayoutPolicy.Choose(300,144,320,new double[]{50,60});Assert.Equal(2,result.Count);Assert.False(result.ShowLabels);Assert.True(HeaderLayoutPolicy.Choose(460,144,320,new double[]{50,60}).ShowLabels);}
    [Fact]
    public void ExpansionHasHysteresisButShrinkingImmediatelyFits()
    {
        var compact=HeaderLayoutPolicy.Choose(450,144,320,new double[]{70,80,90,100});
        Assert.False(HeaderLayoutPolicy.Choose(625,144,320,new double[]{70,80,90,100},compact).ShowLabels);
        var full=HeaderLayoutPolicy.Choose(640,144,320,new double[]{70,80,90,100},compact);Assert.True(full.ShowLabels);
        var shrunk=HeaderLayoutPolicy.Choose(440,144,320,new double[]{70,80,90,100},full);Assert.Equal(2,shrunk.Count);Assert.False(shrunk.ShowLabels);
    }
    [Fact]
    public void NoActionsReservesNoOverflowButtonAndUsesFullLabelsWhenPossible()
    {var result=HeaderLayoutPolicy.Choose(321,144,320,Array.Empty<double>());Assert.True(result.ShowLabels);Assert.Equal(320,result.RequiredWidth);Assert.Equal(0,result.Count);}
}
