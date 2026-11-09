using System.Security.Cryptography;
using HybridMount.Models.Mounting;

namespace HybridMount.Helpers.IO;

/// <summary>借用不可变上传流，统计实际读取量与摘要；释放包装器不关闭调用者流。</summary>
internal sealed class UploadProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly IProgress<UploadProgress>? _progress;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly long _start;
    private long _bytes;
    private string? _digest;
    public UploadProgressStream(Stream inner, IProgress<UploadProgress>? progress)
    { _inner=inner;_progress=progress;_start=inner.CanSeek?inner.Position:0; }
    public long BytesRead=>_bytes;
    public string Digest => _digest ??= Convert.ToHexString(_hash.GetHashAndReset());
    private void Received(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length==0)return;
        if(_digest is not null)throw new IOException("摘要发布后不能继续读取上传流。");
        _hash.AppendData(bytes);_bytes+=bytes.Length;_progress?.Report(new(_bytes,CanSeek?Length-_start:-1));
    }
    public override int Read(byte[] buffer,int offset,int count){var read=_inner.Read(buffer,offset,count);Received(buffer.AsSpan(offset,read));return read;}
    public override int Read(Span<byte> buffer){var read=_inner.Read(buffer);Received(buffer[..read]);return read;}
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
    {var read=await _inner.ReadAsync(buffer,token).ConfigureAwait(false);Received(buffer.Span[..read]);return read;}
    public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)
    {return await ReadAsync(buffer.AsMemory(offset,count),token).ConfigureAwait(false);}
    public override bool CanRead=>_inner.CanRead;
    public override bool CanSeek=>_inner.CanSeek;
    public override bool CanWrite=>false;
    public override long Length=>_inner.Length;
    public override long Position{get=>_inner.Position;set=>Seek(value,SeekOrigin.Begin);}
    public override long Seek(long offset,SeekOrigin origin)
    {
        var position=_inner.Seek(offset,origin);
        if(position==_start){_hash.GetHashAndReset();_bytes=0;_digest=null;}
        else if(position!=_start+_bytes)throw new IOException("上传流只能顺序读取或重置到起点。");
        return position;
    }
    public override void Flush() { }
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    protected override void Dispose(bool disposing){if(disposing)_hash.Dispose();base.Dispose(disposing);}
}
