using System.Text;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>以真实 AWS SDK 请求和本地 S3 服务复现复制回执后同长度替换，禁止错误删源。</summary>
public sealed class P1S3MoveTests
{
    /// <summary>复制及下载上传两条路径均核对本次回执；变化、缺失验证器、失败和取消都保留源。</summary>
    [Theory(Timeout=120000)]
    [InlineData(false,"Content")]
    [InlineData(true,"Content")]
    [InlineData(false,"Version")]
    [InlineData(true,"Version")]
    [InlineData(false,"WeakETag")]
    [InlineData(true,"WeakETag")]
    [InlineData(false,"MissingETag")]
    [InlineData(true,"MissingETag")]
    [InlineData(false,"HeadFailure")]
    [InlineData(true,"HeadFailure")]
    [InlineData(false,"Cancel")]
    [InlineData(true,"Cancel")]
    [InlineData(false,"Directory")]
    [InlineData(true,"Directory")]
    public async Task ChangedOrUnverifiableDestinationNeverDeletesSource(bool fallback,string failure)
    {
        await using var fixture=new S3Fixture{IgnoreCopyConditions=fallback};fixture.Put("remote/source.bin",[1,2,3]);using var cancel=new CancellationTokenSource();
        var handler=fixture.Server.Handler;var written=false;var heads=0;var changed=false;
        fixture.Server.Handler=async request=>
        {
            var target=new Uri(fixture.Server.Address,request.Target).AbsolutePath=="/test-bucket/remote/moved.bin";
            if(target&&written&&request.Method=="HEAD"&&++heads==(fallback?2:1))
            {
                if(failure=="HeadFailure")return HttpTestServer.Response.Text(503,"<Error><Code>ServiceUnavailable</Code><RequestId>test</RequestId></Error>");
                if(failure=="Cancel")cancel.Cancel();
            }
            var response=await handler(request);
            if(target&&request.Method=="PUT"&&response.Status==200)
            {
                written=true;
                if(failure is "MissingETag" or "WeakETag")
                {
                    var tag=failure=="WeakETag"?"W/\"weak-copy\"":null;
                    if(tag is not null)fixture.Objects["remote/moved.bin"]=fixture.Objects["remote/moved.bin"] with{ETag=tag};
                    if(fallback){if(tag is null)response.Headers!.Remove("ETag");else response.Headers!["ETag"]=tag;}
                    else
                    {
                        var xml=XElement.Parse(Encoding.UTF8.GetString(response.Body));var field=xml.Element(xml.Name.Namespace+"ETag")!;
                        if(tag is null)field.Remove();else field.Value=tag;
                        response=response with{Body=Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting))};
                    }
                }
                if(!fallback)Change();
            }
            // 上传返回前的首次 HEAD 仍返回本次上传的元数据，移动额外核对必须发现随后替换。
            if(fallback&&target&&written&&request.Method=="HEAD"&&heads==1)Change();
            return response;
        };
        void Change()
        {
            if(changed||failure is not("Content" or "Version" or "Directory"))return;changed=true;
            if(failure=="Content")fixture.Put("remote/moved.bin",[9,8,7]);
            else if(failure=="Directory"){fixture.Objects.TryRemove("remote/moved.bin",out _);fixture.Put("remote/moved.bin/",[]);}
            else
            {
                var original=fixture.Objects["remote/moved.bin"];var replacement=original with{VersionId="concurrent-version"};fixture.Objects["remote/moved.bin"]=replacement;fixture.Versions[("remote/moved.bin",replacement.VersionId)]=replacement;
            }
        }
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("source.bin");
        var error=await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync(entry.Path,"moved.bin",entry.Version,cancel.Token));
        Assert.Contains(error.Results,item=>item.Path=="source.bin"&&item.Message.Contains("moved.bin",StringComparison.Ordinal));
        Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/source.bin"].Bytes);
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&new Uri(fixture.Server.Address,request.Target).AbsolutePath=="/test-bucket/remote/source.bin");
        if(failure=="Content"){Assert.True(changed);Assert.Equal(new byte[]{9,8,7},fixture.Objects["remote/moved.bin"].Bytes);}
        if(failure=="Version")Assert.Equal("concurrent-version",fixture.Objects["remote/moved.bin"].VersionId);
    }

    /// <summary>目标已确认但服务忽略条件删除时，明确报告能力限制，保留源且不发送用户源 DELETE。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedConditionalDeleteReportsVerifiedTargetAndPreservesSource(bool fallback)
    {
        await using var fixture=new S3Fixture{IgnoreCopyConditions=fallback};fixture.Put("remote/source.bin",[1,2,3]);var handler=fixture.Server.Handler;
        fixture.Server.Handler=request=>
        {
            if(request.Method=="DELETE"&&request.Headers.GetValueOrDefault("If-Match")=="\"hybridmount-never-match\"")
            {
                var key=Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath)["/test-bucket/".Length..];
                Assert.Contains(".hybridmount-probe-",key,StringComparison.Ordinal);fixture.Objects.TryRemove(key,out _);return Task.FromResult(new HttpTestServer.Response(204,[]));
            }
            return handler(request);
        };
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("source.bin");
        var error=await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync(entry.Path,"moved.bin",entry.Version));
        Assert.Equal(CapabilitySupport.Unsupported,source.WriteCapabilities.ConditionalDelete);
        Assert.Contains(error.Results,item=>item.Status=="Copied"&&item.Message.Contains("条件删除验证",StringComparison.Ordinal));
        Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/source.bin"].Bytes);Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/moved.bin"].Bytes);
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&new Uri(fixture.Server.Address,request.Target).AbsolutePath=="/test-bucket/remote/source.bin");
    }

    /// <summary>未返回可查询 VersionId 的兼容服务仍按可靠 ETag 完成正常移动，不永久删除历史版本。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReliableEtagAllowsMoveWhenDestinationVersionIsUnavailable(bool fallback)
    {
        await using var fixture=new S3Fixture{IgnoreCopyConditions=fallback};fixture.Put("remote/source.bin",[1,2,3]);var handler=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            var response=await handler(request);
            if(new Uri(fixture.Server.Address,request.Target).AbsolutePath=="/test-bucket/remote/moved.bin")response.Headers?.Remove("x-amz-version-id");
            return response;
        };
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("source.bin");var moved=await source.MoveAsync(entry.Path,"moved.bin",entry.Version);
        Assert.Equal(3,moved.Size);Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/moved.bin"].Bytes);Assert.False(fixture.Objects.ContainsKey("remote/source.bin"));
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&request.Target.Contains("versionId=",StringComparison.Ordinal));
    }
}
