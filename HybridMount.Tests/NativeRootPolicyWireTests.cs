using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>原生CFAPI高位策略及故障状态须无损跨JSON；只测线格式，不创建或修改同步根。</summary>
public sealed class NativeRootPolicyWireTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void SignedLegacyAndUnsignedPoliciesPreserveHighBits(bool legacy)
    {
        var data=System.Text.Json.JsonSerializer.SerializeToElement(new{hydration=0,modifier=0,population=0,inSync=legacy?-2147483648L:2147483648L,hardlink=0,status=legacy?-1073741823L:3221225473L,provider="test",version="1",identity="",fileId="42"});
        var facts=CloudRootPolicy.Parse(data);Assert.Equal(0x80000000u,facts.InSync);Assert.Equal(0xc0000001u,facts.Status);Assert.Equal(42,facts.FileId);
    }
    [Fact]
    public void OutOfRangePolicyDoesNotBecomeAValidState()
    {
        var data=System.Text.Json.JsonSerializer.SerializeToElement(new{hydration=0,modifier=0,population=0,inSync=4294967296L,hardlink=0,status=0,provider="test",version="1",identity="",fileId="42"});
        Assert.Throws<FormatException>(()=>CloudRootPolicy.Parse(data));
    }
}
