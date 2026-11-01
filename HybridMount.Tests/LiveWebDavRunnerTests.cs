using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>本地真实 HTTP／CFAPI 验证长时测试器的功能矩阵、取消和精确清理，不将短时自检算作真实服务器长测。</summary>
public sealed class LiveWebDavRunnerTests
{
    /// <summary>完成测试矩阵及至少一轮循环后主动停止，确保准确报告取消并清理本轮根、注册和远端数据。</summary>
    [Theory(Timeout=180000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRunnerCanBeCanceledWithoutClaimingRequestedDurationOrLeakingData(bool provisionalWeakTag)
    {
        await using var fixture=new DavFixture();var metadataReads=0;var handler=fixture.Server.Handler;
        if(provisionalWeakTag)fixture.Server.Handler=async request=>
        {
            var response=await handler(request);
            // 服务在写入后的核对中先提供弱标签，稍后对同一内容提供强标签；测试观察者不能要求两者令牌相等。
            if(request.Method=="PROPFIND"&&request.Headers.GetValueOrDefault("Depth")=="0"&&response.Status==207&&
                new Uri(fixture.Server.Address,request.Target).AbsolutePath.EndsWith("/local-delete.txt",StringComparison.Ordinal)&&Interlocked.Increment(ref metadataReads)<=2)
            {
                var xml=XElement.Parse(Encoding.UTF8.GetString(response.Body));
                foreach(var tag in xml.Descendants(XName.Get("getetag","DAV:")))tag.Value="W/"+tag.Value;
                return response with{Body=Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting))};
            }
            return response;
        };
        var initial=fixture.Objects.Keys.Order(StringComparer.Ordinal).ToArray();var id=Guid.NewGuid();var work=Path.Combine(fixture.Area,"live-runner");Directory.CreateDirectory(work);
        new CredentialStore(work).Save(id,new("user","app-password"));using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var running=LiveWebDavRunner.RunAsync(fixture.Options.Endpoint,work,id,60,cancel.Token);var report=Path.Combine(work,"live-report.json");var ready=false;var until=DateTimeOffset.UtcNow.AddSeconds(90);
        try
        {
            while(DateTimeOffset.UtcNow<until&&!running.IsCompleted)
            {
                if(File.Exists(report))
                {
                    using var input=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var json=await JsonDocument.ParseAsync(input);
                    ready=json.RootElement.GetProperty("Phase").GetString()=="ContinuousSoak"&&json.RootElement.GetProperty("Results").EnumerateArray().Any(item=>item.GetProperty("Name").GetString()=="round 1 filesystem upload and digest"&&item.GetProperty("State").GetString()=="Passed");
                    if(ready)break;
                }
                await Task.Delay(100);
            }
        }
        finally{await cancel.CancelAsync();}
        await running.WaitAsync(TimeSpan.FromSeconds(45));using var result=JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.True(ready,result.RootElement.GetRawText());Assert.Equal("Canceled",result.RootElement.GetProperty("Phase").GetString());Assert.True(result.RootElement.GetProperty("ElapsedSeconds").GetDouble()<120);
        Assert.Equal(0,result.RootElement.GetProperty("Failed").GetInt32());Assert.Contains(result.RootElement.GetProperty("Results").EnumerateArray(),item=>item.GetProperty("Name").GetString()=="precise cleanup"&&item.GetProperty("State").GetString()=="Passed");
        if(provisionalWeakTag)Assert.True(metadataReads>=3);
        Assert.Equal(initial,fixture.Objects.Keys.Order(StringComparer.Ordinal).ToArray());Assert.Null(MountPathGuard.ReadRoot(Path.Combine(work,"mount")));
        using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HybridMount.NET.Tests\LiveDav-"+id.ToString("N"));Assert.Null(key);
    }
}
