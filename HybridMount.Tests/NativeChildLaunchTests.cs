using HybridMount.Services.Runtime;

namespace HybridMount.Tests;

/// <summary>严格父子身份与无根原生握手；仅使用隔离编译宿主，不注册同步根。</summary>
public sealed class NativeChildLaunchTests
{
    [Theory]
    [InlineData("package")][InlineData("image")][InlineData("user")][InlineData("session")]
    public void AnyIdentityMismatchIsRejected(string field)
    {
        var proof=new NativeChildIdentity("package","package",@"C:\app\HybridMount.Stock.exe",@"C:\app\HybridMount.Stock.exe","sid","sid",1,1);
        Assert.True(proof.Matches);
        proof=field switch{"package"=>proof with{ActualPackage="different"},"image"=>proof with{ActualImage=@"C:\other\HybridMount.Stock.exe"},"user"=>proof with{ActualUser="other"},_=>proof with{ActualSession=2}};
        Assert.Throws<IOException>(()=>NativeProcessIdentity.Validate(proof));
        Assert.Throws<IOException>(()=>NativeProcessIdentity.Validate(proof with{ExpectedImage="",ActualImage=""}));
    }
    [Fact(Timeout=20000)]
    public async Task SuspendedLaunchAndAuthenticatedHandshakeKeepActualParentIdentity()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
        using var child=System.Diagnostics.Process.GetProcessById(runtime.ProcessId);
        var proof=NativeProcessIdentity.Verify(child,Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe"),"NativeRuntime");
        Assert.True(proof.Matches);Assert.NotEqual(Environment.ProcessId,child.Id);Assert.True(runtime.IsAvailable);
    }
}
