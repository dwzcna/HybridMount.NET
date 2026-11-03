using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>协议内按职责拆分的 Models 实现；公开入口与版本字段保持兼容。</summary>
public sealed partial class S3FileSource
{
    private sealed record ObjectVersion(string? ETag, string? VersionId, bool Virtual = false);
    private sealed record UploadRecord(string Bucket, string Key, string UploadId);
    /// <summary>本地表单提交前校验，错误不持久化成不可编辑的挂载根。</summary>
    public static Uri ValidateOptions(FileSourceConfiguration options)
    {
        var endpoint=FileSourceFactory.Endpoint(options);
        if(endpoint.AbsolutePath!="/"||options.Bucket.Length is <3 or >63||options.Bucket.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('.' or '-'))||
            string.IsNullOrWhiteSpace(options.Region)||options.Region.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))
            throw new ArgumentException("请检查 S3 接入点、桶名与签名区域；接入点只包含协议与主机。");
        if(options.Prefix.Length!=0)RelativePaths.Normalize(options.Prefix.Trim('/'));
        return endpoint;
    }
    private string Key(string path) => _prefix + RelativePaths.Normalize(path);
    private static string NonRoot(string path) => RelativePaths.Normalize(path) is { Length: > 0 } result ? result : throw new ArgumentException("不能修改源根。");
    private static ObjectVersion Version(SourceEntry entry) => JsonSerializer.Deserialize<ObjectVersion>(entry.Version) ?? throw new IOException("S3 版本无效。");
    private static ObjectVersion Version(string expected) => JsonSerializer.Deserialize<ObjectVersion>(expected) ?? throw new IOException("S3 版本无效。");
    private static string RequiredETag(ObjectVersion version) => EntityTagHeaderValue.TryParse(version.ETag, out var tag) && !tag.IsWeak && tag.Tag!="*"
        ? version.ETag! : throw new NotSupportedException("S3 对象缺少有效 ETag，拒绝条件修改。");
    private static string? ImmutableVersion(string? value) => string.IsNullOrEmpty(value) || value == "null" ? null : value;

}
