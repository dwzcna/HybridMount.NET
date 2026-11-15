using System.Net;
using System.Security.Cryptography;

namespace HybridMount.Infrastructure.Protocols.WebDAV;

/// <summary>仅绑定同一有效URI的identity表示；摘要用于内容比较，绝不授权条件写或不受保护的范围读取。</summary>
internal sealed record WebDavRepresentationDigest(string Algorithm,string Base64,string EffectiveUri)
{
    internal bool Matches(string sha256,string? sha512=null)=>Convert.ToHexString(Convert.FromBase64String(Base64))==
        (Algorithm=="sha-256"?sha256:sha512);
}

/// <summary>RFC9530字典解析；严格校验字节序列、重复成员、参数语法及已知算法长度，有界接受未知算法。</summary>
internal static class WebDavReprDigest
{
    internal static WebDavRepresentationDigest? Parse(IEnumerable<string> fields,Uri uri)
    {
        var text=string.Join(",",fields);if(text.Length==0)return null;
        if(text.Length>16384)throw Invalid();
        var reader=new Fields(text);var members=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        while(!reader.End)
        {
            var key=reader.Key();reader.Require('=');var bytes=reader.Bytes();
            if(!members.TryAdd(key,bytes))throw Invalid();
            reader.Parameters();if(reader.End)break;reader.Require(',');if(reader.End)throw Invalid();
        }
        foreach(var (algorithm,length) in new[]{("sha-256",32),("sha-512",64)})
            if(members.TryGetValue(algorithm,out var value)&&value.Length!=length)throw Invalid();
        foreach(var algorithm in new[]{"sha-256","sha-512"})
            if(members.TryGetValue(algorithm,out var value))return new(algorithm,Convert.ToBase64String(value),uri.AbsoluteUri);
        return null;
    }
    internal static IOException Invalid()=>new WebDavDigestException("WebDAV Repr-Digest格式或算法长度无效。");
    private sealed class Fields(string text)
    {
        private int _at;
        private void Space(){while(_at<text.Length&&text[_at] is ' ' or '\t')_at++;}
        internal bool End{get{Space();return _at==text.Length;}}
        internal void Require(char c){Space();if(_at==text.Length||text[_at++]!=c)throw Invalid();}
        internal string Key()
        {
            Space();var start=_at;
            if(_at==text.Length||!(text[_at] is >= 'a' and <= 'z' or '*'))throw Invalid();
            _at++;while(_at<text.Length&&(text[_at] is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' or '*'))_at++;
            return text[start.._at];
        }
        internal byte[] Bytes()
        {
            Require(':');var start=_at;
            while(_at<text.Length&&text[_at]!=':')
            {if(!(char.IsAsciiLetterOrDigit(text[_at])||text[_at] is '+' or '/' or '='))throw Invalid();_at++;}
            if(_at==text.Length)throw Invalid();var raw=text[start.._at++];
            try{return Convert.FromBase64String(raw.PadRight(raw.Length+(4-raw.Length%4)%4,'='));}catch(FormatException){throw Invalid();}
        }
        internal void Parameters()
        {
            var keys=new HashSet<string>(StringComparer.Ordinal);
            while(!End&&text[_at]==';')
            {
                _at++;if(!keys.Add(Key()))throw Invalid();Space();if(_at==text.Length||text[_at]!='=')continue;_at++;Space();
                if(_at==text.Length)throw Invalid();
                if(text[_at]==':'){Bytes();continue;}
                if(text[_at]=='"')
                {
                    _at++;var closed=false;
                    while(_at<text.Length)
                    {
                        var c=text[_at++];if(c=='"'){closed=true;break;}
                        if(c=='\\'){if(_at==text.Length||text[_at] is not('"' or '\\'))throw Invalid();_at++;}
                        else if(c<0x20||c>0x7e)throw Invalid();
                    }
                    if(!closed)throw Invalid();continue;
                }
                var start=_at;while(_at<text.Length&&text[_at] is not(';' or ',' or ' ' or '\t'))_at++;
                var item=text[start.._at];
                if(item.Length==0||!System.Text.RegularExpressions.Regex.IsMatch(item,@"^(?:\?[01]|-?[0-9]{1,15}|-?[0-9]{1,12}\.[0-9]{1,3}|[A-Za-z*][A-Za-z0-9!#$%&'*+\-.^_`|~:/]*)$",System.Text.RegularExpressions.RegexOptions.CultureInvariant))throw Invalid();
            }
        }
    }
}

/// <summary>完整GET在真正EOF核验头／尾摘要；释放未读完响应不会宣称已验证，消费失败不进入持久缓存。</summary>
internal sealed class WebDavDigestException(string message):IOException(message);
internal sealed class WebDavDigestContent:HttpContent
{
    private readonly HttpContent _inner;private readonly HttpResponseMessage _response;private readonly Uri _uri;
    internal WebDavDigestContent(HttpResponseMessage response,Uri uri)
    {
        _inner=response.Content;_response=response;_uri=uri;
        _=Read(response.Headers);foreach(var field in _inner.Headers)Headers.TryAddWithoutValidation(field.Key,field.Value);
    }
    private WebDavRepresentationDigest? Read(System.Net.Http.Headers.HttpHeaders headers)=>
        WebDavReprDigest.Parse(headers.TryGetValues("Repr-Digest",out var values)?values:[],_uri);
    protected override bool TryComputeLength(out long length){length=_inner.Headers.ContentLength??-1;return length>=0;}
    protected override async Task<Stream> CreateContentReadStreamAsync()=>await CreateContentReadStreamAsync(default).ConfigureAwait(false);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token)=>new VerifiedStream(await _inner.ReadAsStreamAsync(token).ConfigureAwait(false),()=>new[]{Read(_response.Headers),Read(_response.TrailingHeaders)});
    protected override async Task SerializeToStreamAsync(Stream target,TransportContext? context)
    {await using var source=await CreateContentReadStreamAsync().ConfigureAwait(false);await source.CopyToAsync(target).ConfigureAwait(false);}
    protected override void Dispose(bool disposing){if(disposing)_inner.Dispose();base.Dispose(disposing);}
    private sealed class VerifiedStream(Stream inner,Func<WebDavRepresentationDigest?[]> digests):Stream
    {
        private readonly IncrementalHash _sha256=IncrementalHash.CreateHash(HashAlgorithmName.SHA256),_sha512=IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        private bool _complete;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {
            var count=await inner.ReadAsync(buffer,token).ConfigureAwait(false);if(buffer.Length==0)return count;
            if(count>0){_sha256.AppendData(buffer.Span[..count]);_sha512.AppendData(buffer.Span[..count]);}
            else if(!_complete)
            {
                _complete=true;var first=Convert.ToHexString(_sha256.GetHashAndReset());var second=Convert.ToHexString(_sha512.GetHashAndReset());
                if(digests().Any(digest=>digest is not null&&!digest.Matches(first,second)))throw new WebDavDigestException("WebDAV完整正文与Repr-Digest不符，未提交缓存或回执。");
            }
            return count;
        }
        public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        protected override void Dispose(bool disposing){if(disposing){inner.Dispose();_sha256.Dispose();_sha512.Dispose();}base.Dispose(disposing);}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
