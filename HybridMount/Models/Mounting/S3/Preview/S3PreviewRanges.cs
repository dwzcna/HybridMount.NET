using System.Net;
using System.Net.Http.Headers;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class S3FileSource
{
    /// <summary>S3 原读取始终校验范围和版本且从不整文件退化；缺少可靠版本时声明不支持预览。</summary>
    public async ValueTask<PreviewRangeResult> TryReadPreviewRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {
        try{_ = RequiredETag(Version(entry));}catch(NotSupportedException){return new(false,0);}
        return new(true,await ReadRangeAsync(entry,offset,buffer,token).ConfigureAwait(false));
    }
}
