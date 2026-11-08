using System.Runtime.InteropServices;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>生产卷未附加过滤器时保留失败及原码，明确区分驱动运行与卷实例；不改变系统服务。</summary>
public sealed class CloudFilesAvailabilityTests
{
    [Fact]
    public void UnsupportedRootQueryExplainsVolumeAttachmentAndRemainsAFailure()
    {
        var error=Assert.Throws<COMException>(()=>CloudFilesNative.CheckRootQueryResult(unchecked((int)0x80070001),@"D:\MyDav"));
        Assert.Equal(unchecked((int)0x80070001),error.HResult);Assert.Contains("同步根查询",error.Message);Assert.Contains("RUNNING",error.Message);Assert.Contains("fltmc.exe attach CldFlt D:",error.Message);
    }
    [Fact]
    public void OtherRootQueryFailuresKeepTheirOriginalErrorAndSuccessDoesNotThrow()
    {
        var error=Assert.Throws<UnauthorizedAccessException>(()=>CloudFilesNative.CheckRootQueryResult(unchecked((int)0x80070005),@"D:\MyDav"));
        Assert.Equal(unchecked((int)0x80070005),error.HResult);CloudFilesNative.CheckRootQueryResult(0,@"D:\MyDav");
    }
}
