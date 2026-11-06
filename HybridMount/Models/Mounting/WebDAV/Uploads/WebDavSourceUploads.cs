using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>协议内按职责拆分的 Uploads 实现；公开入口与版本字段保持兼容。</summary>
public sealed partial class WebDavFileSource
{
    private static void Condition(HttpRequestMessage request, string? expectedVersion)
    {
        if (expectedVersion is null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else
        {
            var version = JsonSerializer.Deserialize<VersionInfo>(expectedVersion);
            if (!Strong(version?.ETag)) throw new NotSupportedException("此条目无强 ETag，拒绝条件修改。");
            request.Headers.IfMatch.ParseAdd(NormalizeTag(version!.ETag)!);
        }
    }
    public async Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token = default)
    {
        path = NonRoot(path);
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), Address(path, true)); Condition(request, null);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false);
        if(response.StatusCode==HttpStatusCode.BadRequest)throw new SourceRequestRejectedException(400,"WebDAV 拒绝创建目录（HTTP 400）；请确认目标父目录允许写入，部分服务的账户根不能新建普通目录。");
        Success(response);
        try { return await GetMetadataAsync(path, token).ConfigureAwait(false); }
        catch(Exception error)when(error is IOException or OperationCanceledException){throw new SourceOutcomeUnknownException("写入后核对",new IOException("远端成功响应后未能读取元数据。"));}
    }
    public async Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); destination = NonRoot(destination);
        if (RelativePaths.Contains(path, destination)) throw new ArgumentException("不能移入自身。");
        await VerifyMutationAsync("MOVE",RelativePaths.Parent(destination),token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), Address(path)); Condition(request, expectedVersion);
        request.Headers.Add("Destination", Address(destination).AbsoluteUri); request.Headers.Add("Overwrite", "F");
        using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response);
        try { return await GetMetadataAsync(destination, token).ConfigureAwait(false); }
        catch(Exception error)when(error is IOException or OperationCanceledException){throw new SourceOutcomeUnknownException("移动后核对",new IOException("远端成功响应后未能读取元数据。"));}
    }
    public async Task DeleteAsync(string path, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyMutationAsync("DELETE",RelativePaths.Parent(path),token).ConfigureAwait(false);
        var metadata = await GetMetadataAsync(path, token).ConfigureAwait(false);
        if (metadata.IsDirectory)
            await foreach (var _ in ListAsync(path, token).ConfigureAwait(false)) throw new NotSupportedException("首版仅允许删除空远端目录，避免递归条件覆盖不完整。");
        using var request = new HttpRequestMessage(HttpMethod.Delete, Address(path, metadata.IsDirectory)); Condition(request, expectedVersion);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response);
    }
    private static string NonRoot(string path) => RelativePaths.Normalize(path) is { Length: > 0 } normalized ? normalized : throw new ArgumentException("不能修改源根。");

}
