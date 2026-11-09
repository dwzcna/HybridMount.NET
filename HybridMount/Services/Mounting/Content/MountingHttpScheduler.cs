using System.Net;
namespace HybridMount.Services.Mounting;

/// <summary>内建协议的唯一网络执行位入口；只读响应正文由包装器拥有，直到完全消费或关闭才归还。</summary>
internal static class MountingHttpScheduler
{
    internal static Task<TasksQueueService.NetworkLease?> AcquireAsync(MountQueueBinding? binding,Models.Mounting.MountingPriority priority,CancellationToken token)
    {
        if(binding is null)return Task.FromResult<TasksQueueService.NetworkLease?>(null);
        var ambient=TasksQueueService.CurrentTask;
        var task=ambient is not null&&ambient.MountId==binding.MountId&&ambient.Generation==binding.Generation?ambient:
            new Models.Mounting.MountingTask(Guid.NewGuid().ToString("N"),binding.MountId,binding.Generation,
                priority,
                DateTimeOffset.UtcNow,"network");
        return Acquire();
        async Task<TasksQueueService.NetworkLease?> Acquire()=>await binding.Queue.AcquireAsync(binding,task,token).ConfigureAwait(false);
    }
    internal static async Task<HttpResponseMessage> SendAsync(MountQueueBinding? binding,Models.Mounting.MountingPriority priority,Func<CancellationToken,Task<HttpResponseMessage>> send,CancellationToken token)
    {
        var lease=await AcquireAsync(binding,priority,token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();var response=await send(token).ConfigureAwait(false);
            if(lease is not null){response.Content=new LeasedContent(response.Content,lease);lease=null;}
            return response;
        }
        finally{lease?.Dispose();}
    }
    internal static void OwnResponse(HttpResponseMessage response,TasksQueueService.NetworkLease? lease)
    {if(lease is not null)response.Content=new LeasedContent(response.Content,lease);}
    /// <summary>保留全部HTTP内容头；缓冲读取和流读取共用幂等租约，不在响应头到达时归还。</summary>
    private sealed class LeasedContent:HttpContent
    {
        private readonly HttpContent _inner;
        private readonly TasksQueueService.NetworkLease _lease;
        public LeasedContent(HttpContent inner,TasksQueueService.NetworkLease lease)
        {_inner=inner;_lease=lease;foreach(var header in inner.Headers)Headers.TryAddWithoutValidation(header.Key,header.Value);}
        protected override bool TryComputeLength(out long length){length=_inner.Headers.ContentLength??0;return _inner.Headers.ContentLength.HasValue;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>SerializeToStreamAsync(stream,context,CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? context,CancellationToken token)
        {try{await _inner.CopyToAsync(stream,context,token).ConfigureAwait(false);}finally{_inner.Dispose();_lease.Dispose();}}
        protected override Task<Stream> CreateContentReadStreamAsync()=>CreateContentReadStreamAsync(CancellationToken.None);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token)
        {try{return new LeasedStream(await _inner.ReadAsStreamAsync(token).ConfigureAwait(false),_lease);}catch{_inner.Dispose();_lease.Dispose();throw;}}
        protected override void Dispose(bool disposing){if(disposing){try{_inner.Dispose();}finally{_lease.Dispose();}}base.Dispose(disposing);}
    }
    /// <summary>只读网络流；EOF、异常或显式关闭都先关闭正文，再幂等归还执行位。</summary>
    private sealed class LeasedStream(Stream inner,TasksQueueService.NetworkLease lease):Stream
    {
        private int _finished;
        public override bool CanRead=>inner.CanRead;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        private void Finish(){if(Interlocked.Exchange(ref _finished,1)!=0)return;try{inner.Dispose();}finally{lease.Dispose();}}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {if(Volatile.Read(ref _finished)!=0)return 0;try{var count=await inner.ReadAsync(buffer,token).ConfigureAwait(false);if(count==0&&buffer.Length>0)Finish();return count;}catch{Finish();throw;}}
        public override int Read(byte[] bytes,int offset,int count)=>ReadAsync(bytes.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        public override int Read(Span<byte> buffer){if(Volatile.Read(ref _finished)!=0)return 0;try{var count=inner.Read(buffer);if(count==0&&buffer.Length>0)Finish();return count;}catch{Finish();throw;}}
        public override Task<int> ReadAsync(byte[] bytes,int offset,int count,CancellationToken token)=>ReadAsync(bytes.AsMemory(offset,count),token).AsTask();
        protected override void Dispose(bool disposing){if(disposing)Finish();base.Dispose(disposing);}
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long length)=>throw new NotSupportedException();
        public override void Write(byte[] bytes,int offset,int count)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();
    }
}
