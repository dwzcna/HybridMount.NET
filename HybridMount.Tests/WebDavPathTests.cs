using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>模拟严格区分文件与集合 URI 的 DAV 服务，覆盖真实 NAS 上传回执核对失败的路径。</summary>
public sealed class WebDavPathTests
{
    /// <summary>深度 0 查询不能给文件追加斜杠；上传、缺失查询及临时清理使用同一正确路径。</summary>
    [Theory]
    [InlineData("empty.bin",0)]
    [InlineData("中文 #%.bin",16397)]
    public async Task StrictFileUrisSupportUploadReadbackAndTemporaryCleanup(string name,int size)
    {
        await using var fixture=new DavFixture();var handler=fixture.Server.Handler;
        fixture.Server.Handler=request=>
        {
            var uri=new Uri(fixture.Server.Address,request.Target);var path=Uri.UnescapeDataString(uri.AbsolutePath[5..]).TrimEnd('/');
            if(request.Method=="PROPFIND"&&uri.AbsolutePath.EndsWith('/')&&
                (!fixture.Objects.TryGetValue(path,out var item)||!item.Directory))
                return Task.FromResult(new HttpTestServer.Response(400,[]));
            return handler(request);
        };
        await using var source=fixture.Source();await source.ProbeAsync();await source.CreateDirectoryAsync("目录");
        var data=Enumerable.Range(0,size).Select(i=>(byte)(i*17)).ToArray();var path="目录/"+name;
        var uploaded=await source.UploadAsync(path,new MemoryStream(data),null);
        Assert.False(uploaded.IsDirectory);Assert.Equal(size,uploaded.Size);Assert.Equal(data,fixture.Objects[path].Bytes);
        Assert.Null(await source.TryGetMetadataAsync("目录/missing.bin"));
        var directory=await source.GetMetadataAsync("目录");Assert.True(directory.IsDirectory);
        var listed=new List<string>();await foreach(var item in source.ListAsync("目录"))listed.Add(item.Path);
        Assert.Contains(path,listed);Assert.Empty(source.PendingTemporaryItems);
        var bytes=new byte[size];Assert.Equal(size,await source.ReadRangeAsync(uploaded,0,bytes));Assert.Equal(data,bytes);
        Assert.Contains(fixture.Server.Requests,request=>request.Method=="PROPFIND"&&request.Headers.GetValueOrDefault("Depth")=="1"&&request.Target.EndsWith('/'));
    }
}
