using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;

namespace HybridMount.Infrastructure.Protocols.WebDAV;

/// <summary>只对PROPFIND／REPORT手动解码；64MiB展开上限叠加已有16Mi字符XML限制，流和网络额度随响应关闭。</summary>
internal sealed class WebDavMetadataContent:HttpContent
{
    internal const long MaximumDecodedBytes=64L*1024*1024;
    private readonly HttpContent _inner;
    internal WebDavMetadataContent(HttpContent inner)
    {
        _inner=inner;
        if(inner.Headers.ContentEncoding.Count!=1||!inner.Headers.ContentEncoding.Single().Equals("gzip",StringComparison.OrdinalIgnoreCase))
            throw new WebDavMetadataEncodingException("WebDAV元数据响应使用了未支持的内容编码。");
        foreach(var field in inner.Headers)
            if(!field.Key.Equals("Content-Encoding",StringComparison.OrdinalIgnoreCase)&&!field.Key.Equals("Content-Length",StringComparison.OrdinalIgnoreCase))Headers.TryAddWithoutValidation(field.Key,field.Value);
    }
    protected override bool TryComputeLength(out long length){length=0;return false;}
    protected override Task<Stream> CreateContentReadStreamAsync()=>CreateContentReadStreamAsync(default);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token)=>new DecodedStream(await _inner.ReadAsStreamAsync(token).ConfigureAwait(false));
    protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>SerializeToStreamAsync(stream,context,default);
    protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? context,CancellationToken token)
    {await using var decoded=await CreateContentReadStreamAsync(token).ConfigureAwait(false);await decoded.CopyToAsync(stream,token).ConfigureAwait(false);}
    protected override void Dispose(bool disposing){if(disposing)_inner.Dispose();base.Dispose(disposing);}
    /// <summary>保留压缩流末8字节核验CRC及ISIZE，拒绝平台解压器可能容忍的尾部截断；不缓存压缩或展开正文。</summary>
    private sealed class CompressedTailStream(Stream inner):ReadOnlyStream
    {
        private readonly byte[] _tail=new byte[8];internal long Bytes {get;private set;}
        internal byte[] Trailer=>Enumerable.Range(0,8).Select(index=>_tail[(int)((Bytes+index)%8)]).ToArray();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {
            var count=await inner.ReadAsync(buffer,token).ConfigureAwait(false);
            foreach(var value in buffer.Span[..count])_tail[(int)(Bytes++%8)]=value;
            return count;
        }
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
    private sealed class DecodedStream:ReadOnlyStream
    {
        private readonly CompressedTailStream _compressed;private readonly GZipStream _decoder;
        private long _bytes;private uint _crc=uint.MaxValue;private bool _complete;
        internal DecodedStream(Stream inner){_compressed=new(inner);_decoder=new(_compressed,CompressionMode.Decompress);}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {
            int count;
            try{count=await _decoder.ReadAsync(buffer,token).ConfigureAwait(false);}
            catch(InvalidDataException){throw new WebDavMetadataEncodingException("WebDAV元数据gzip损坏或截断，未采用部分目录。");}
            if(buffer.Length==0)return count;
            if(count>0)
            {
                _bytes+=count;if(_bytes>MaximumDecodedBytes)throw new WebDavMetadataEncodingException("WebDAV元数据gzip展开超过64MiB，未采用部分目录。");
                foreach(var value in buffer.Span[..count])_crc=CrcTable[(_crc^value)&255]^(_crc>>8);
            }
            else if(!_complete)
            {
                if(await _compressed.ReadAsync(new byte[1],token).ConfigureAwait(false)!=0||_compressed.Bytes<18)
                    throw new WebDavMetadataEncodingException("WebDAV元数据gzip尾部无效或包含额外内容。");
                var trailer=_compressed.Trailer;
                if(BinaryPrimitives.ReadUInt32LittleEndian(trailer)!=(_crc^uint.MaxValue)||BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4))!=(uint)_bytes)
                    throw new WebDavMetadataEncodingException("WebDAV元数据gzip长度或校验不符，未采用部分目录。");
                _complete=true;
            }
            return count;
        }
        protected override void Dispose(bool disposing){if(disposing)_decoder.Dispose();base.Dispose(disposing);}
    }
    private static readonly uint[] CrcTable=Enumerable.Range(0,256).Select(value=>
    {var crc=(uint)value;for(var bit=0;bit<8;bit++)crc=(crc&1)!=0?0xEDB88320U^(crc>>1):crc>>1;return crc;}).ToArray();
    private abstract class ReadOnlyStream:Stream
    {
        public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
/// <summary>确定的解码错误不可当作短暂网络失败重试或转换为空列表。</summary>
internal sealed class WebDavMetadataEncodingException(string message):IOException(message);
