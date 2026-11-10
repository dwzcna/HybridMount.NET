using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>显式扩展验收：真实 500MiB、隔离解码缩略图和独立原始 DAV 读回，不参与默认单元测试联网。</summary>
public static partial class LiveWebDavRunner
{
    /// <summary>在授权共享运行 60–180 分钟，增加 500MiB 与缩略图；凭据和清理边界与标准入口相同。</summary>
    public static Task<string> RunExtendedAsync(string endpoint,string workDirectory,Guid credentialId,int minutes=75,CancellationToken token=default)
        =>new Session(endpoint,workDirectory,credentialId,minutes,true).RunAsync(token);

    /// <summary>短轮复测远端写入及冷缩略图；报告 FocusedCompleted，不作为 1–3 小时长测完成证据。</summary>
    public static Task<string> RunFocusedAsync(string endpoint,string workDirectory,Guid credentialId,CancellationToken token=default)
        =>new Session(endpoint,workDirectory,credentialId,60,false,true).RunAsync(token);

    private sealed partial class Session
    {
        private sealed record IndependentCheck(string Path,long Bytes,string Sha256,DateTimeOffset CheckedUtc);
        private readonly Dictionary<string,IndependentCheck> _independent=new(StringComparer.Ordinal);
        private string? _previewSample;

        /// <summary>直接经 HttpClient 执行 DAV GET，独立核对响应、长度和 SHA-256，不使用产品文件源读取或本地缓存。</summary>
        private async Task<string> IndependentDigest(string path,SourceEntry metadata,CancellationToken token)
        {
            Require(_known.Contains(path),"独立核对路径没有登记。");
            var uri=RootUri+string.Join('/',RelativePaths.Normalize(path).Split('/').Select(Uri.EscapeDataString));
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            using var version=JsonDocument.Parse(metadata.Version);
            if(version.RootElement.TryGetProperty("ETag",out var field)&&EntityTagHeaderValue.TryParse(field.GetString(),out var tag)&&!tag.IsWeak&&tag.Tag!="*")request.Headers.IfMatch.Add(tag);
            using var response=await _raw!.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            if(response.StatusCode==HttpStatusCode.PreconditionFailed)throw new SourceConflictException("独立 DAV 核对观察到并发版本变化。");
            Require(response.StatusCode==HttpStatusCode.OK&&response.Content.Headers.ContentLength==metadata.Size,"独立 DAV 响应状态或长度不同。");
            await using var input=await response.Content.ReadAsStreamAsync(token);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer=new byte[1024*1024];long total=0,reported=0;int count;
            while((count=await input.ReadAsync(buffer,token))!=0)
            {
                total+=count;Require(total<=metadata.Size,"独立 DAV 正文超过声明长度。");hash.AppendData(buffer,0,count);
                if(total-reported>=64L*1024*1024){reported=total;Console.WriteLine($"{DateTimeOffset.UtcNow:O} DAV verify {path}: {total}/{metadata.Size}");}
            }
            Require(total==metadata.Size,"独立 DAV 正文提前结束。");var digest=Convert.ToHexString(hash.GetHashAndReset());
            _independent[path]=new(path,total,digest,DateTimeOffset.UtcNow);return digest;
        }

        private async Task ExtendedMatrix(CancellationToken token)
        {
            _phase="ExtendedMatrix";Save();
            await Case("500MiB filesystem upload independent DAV SHA256 and CFAPI read",LargeFile,token,TimeSpan.FromMinutes(20));
            await Case("JPEG PNG MP4 EXE cold thumbnails without hydration",Thumbnails,token,TimeSpan.FromMinutes(8));
            await Case("malformed thumbnail safe fallback and switch",MalformedPreview,token);
        }
        /// <summary>流式生成大文件；上传由文件系统触发，远端另经原始 DAV 验证，随后脱水并通过 CFAPI 随机读取。</summary>
        private async Task LargeFile(CancellationToken token)
        {
            const string name="large-500MiB.bin";const long length=500L*1024*1024;Track("mount/"+name);
            var path=Path.Combine(Local,name);var block=Payload(1024*1024,5003);using var expectedHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,block.Length,FileOptions.Asynchronous))
            {
                for(var i=0;i<500;i++){await file.WriteAsync(block,token);expectedHash.AppendData(block);if(i%64==63)Console.WriteLine($"{DateTimeOffset.UtcNow:O} generating 500MiB: {i+1}/500 MiB");}
                await file.FlushAsync(token);file.Flush(true);
            }
            var expected=Convert.ToHexString(expectedHash.GetHashAndReset());var until=DateTimeOffset.UtcNow.AddMinutes(12);LocalUpload? complete=null;long last=-1;
            while(DateTimeOffset.UtcNow<until)
            {
                token.ThrowIfCancellationRequested();var item=_repository!.Uploads(_mount!.Id).FirstOrDefault(x=>x.LocalPath==name&&!x.IsCopy);
                if(item?.State is "Conflict" or "Failed")throw new IOException("500MiB 上传："+item.State+" "+item.Error);
                if(item is not null&&item.Bytes!=last){last=item.Bytes;Console.WriteLine($"{DateTimeOffset.UtcNow:O} 500MiB upload {item.State}: {item.Bytes}/{length}");}
                if(item?.State=="Completed"){complete=item;break;}await Task.Delay(500,token);
            }
            Require(complete is not null,"500MiB 自动上传超时。");
            await using(var independent=Source(RootUri+"mount/","verify-cache"))
            {
                var metadata=await independent.GetMetadataAsync(name,token);Require(metadata.Size==length,"500MiB 元数据长度不符。");
                Require(await IndependentDigest("mount/"+name,metadata,token)==expected,"500MiB 原始 DAV SHA-256 不符。");
            }
            await Job("freeSpace",[path],token);Require(!CloudFilesNative.HasFullContent(path,length),"大文件脱水未生效。");
            await using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.Asynchronous|FileOptions.RandomAccess))
            {
                foreach(var offset in new[]{0L,127L*1024*1024+19,length-4096})
                {
                    input.Position=offset;var actual=new byte[4096];await input.ReadExactlyAsync(actual,token);
                    Require(actual.Select((value,index)=>value==block[(offset+index)%block.Length]).All(x=>x),"500MiB CFAPI 随机读取不同。");
                }
            }
            await Job("keepOnDevice",[path],token);await using(var hydrated=File.OpenRead(path))Require(Convert.ToHexString(await SHA256.HashDataAsync(hydrated,token))==expected,"500MiB 固定水合摘要不符。");
            await Job("freeSpace",[path],token);Require(!CloudFilesNative.HasFullContent(path,length),"500MiB 再次脱水未生效。");
        }
        /// <summary>上传真实合成媒体及带资源 EXE，独立 DAV 核对后测试冷预览，禁止原占位被水合。</summary>
        private async Task Thumbnails(CancellationToken token)
        {
            await _manager!.SetThumbnailsEnabledAsync(_mount!.Id,false,token);
            foreach(var extension in new[]{"jpg","png","mp4","exe"})
            {
                var bytes=extension=="exe"?PreviewExecutable():await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(typeof(LiveWebDavRunner).Assembly.Location)!,"Assets","sample."+extension),token);
                var name="preview-cold."+extension;Track("mount/"+name);await RawUpload("mount/"+name,bytes,null,token);
                var metadata=await _source!.GetMetadataAsync("mount/"+name,token);Require(await IndependentDigest("mount/"+name,metadata,token)==Digest(bytes),"入站媒体 DAV 摘要不符。");
            }
            // Apache 新写入资源可暂用弱标签，刷新获取当前版本，产品仍严格区分强弱标签。
            await Task.Delay(2200,token);await Job("refresh",null,token);
            var paths=new[]{"jpg","png","mp4","exe"}.Select(extension=>Path.Combine(Local,"preview-cold."+extension)).ToArray();
            foreach(var path in paths)Require(File.Exists(path)&&!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length),"远端媒体没有创建冷占位。");
            await _manager.SetThumbnailsEnabledAsync(_mount.Id,true,token);
            foreach(var path in paths)
            {
                _manager.RequestPreview(path,128);byte[]? png=null;var until=DateTimeOffset.UtcNow.AddSeconds(20);
                while(DateTimeOffset.UtcNow<until){token.ThrowIfCancellationRequested();png=_manager.GetPreview(path,128);if(png is not null||_manager.PreviewState(path)=="unsupported")break;await Task.Delay(100,token);}
                Require(!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length),"缩略图水合了原占位。");
                if(png is null)
                {
                    _results.Add(new("cold thumbnail "+Path.GetExtension(path),"CapabilityLimited",0,"服务版本、严格 Range 或解码能力不足；原占位保持未水合。"));
                    await Job("keepOnDevice",[path],token);until=DateTimeOffset.UtcNow.AddSeconds(20);
                    while(DateTimeOffset.UtcNow<until&&(png=_manager.GetPreview(path,128)) is null)await Task.Delay(100,token);
                    Require(png is not null,"完整本地媒体也未生成缩略图。");await Job("freeSpace",[path],token);
                }
                Require(png is {Length:>24},"缩略图为空。");_results.Add(new("thumbnail generated "+Path.GetExtension(path),"Passed",0,$"PNG={png!.Length}; cache remained available after dehydration"));
                _previewSample??=path;
            }
            await PreviewToggle(token);
        }
        /// <summary>只将本产品的 EXE 当作数据源，扩展尾部到 16MiB，不执行远端程序。</summary>
        private static byte[] PreviewExecutable()
        {
            var root=new DirectoryInfo(Path.GetDirectoryName(typeof(LiveWebDavRunner).Assembly.Location)!);
            while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;
            var path=root is null?"":Path.Combine(root.FullName,"HybridMount","bin","x64","Debug","Unpackaged","net10.0-windows10.0.26100.0","win-x64","HybridMount.exe");
            if(!File.Exists(path))path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
            var original=File.ReadAllBytes(path);var result=new byte[Math.Max(16*1024*1024,original.Length)];original.CopyTo(result,0);return result;
        }
        private async Task PreviewToggle(CancellationToken token)
        {
            if(_previewSample is null)throw new CapabilityLimitedException("媒体预览初始化未完成。");
            await _manager!.SetThumbnailsEnabledAsync(_mount!.Id,false,token);Require(_manager.GetPreview(_previewSample,128) is null,"关闭后仍提供缓存。");
            await _manager.SetThumbnailsEnabledAsync(_mount.Id,true,token);_manager.RequestPreview(_previewSample,128);
            var until=DateTimeOffset.UtcNow.AddSeconds(12);while(_manager.GetPreview(_previewSample,128) is null&&DateTimeOffset.UtcNow<until)await Task.Delay(100,token);
            Require(_manager.GetPreview(_previewSample,128) is not null,"重新开启未恢复缓存。");
        }
        /// <summary>仅对已记账的新测试资源执行独立 DAV PUT，创建用 If-None-Match，更新仅用可靠强标签。</summary>
        private async Task RawUpload(string path,byte[] bytes,string? expected,CancellationToken token)
        {
            Require(_known.Contains(path),"远端写入未记账。");var uri=RootUri+string.Join('/',path.Split('/').Select(Uri.EscapeDataString));
            using var request=new HttpRequestMessage(HttpMethod.Put,uri){Content=new ByteArrayContent(bytes)};
            if(expected is null)request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            else{Require(EntityTagHeaderValue.TryParse(expected,out var tag)&&!tag.IsWeak&&tag.Tag!="*","独立 DAV 更新缺少强标签。");request.Headers.IfMatch.ParseAdd(expected);}
            using var response=await _raw!.SendAsync(request,token);Require(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.OK,"独立 DAV 上传被拒绝："+(int)response.StatusCode);
        }
        private async Task RemoteInbound(CancellationToken token)
        {
            const string name="upstream-created.txt";var relative="mount/"+name;Track(relative);var path=Path.Combine(Local,name);var first="remote incoming A"u8.ToArray();var second="remote incoming B modified"u8.ToArray();
            await RawUpload(relative,first,null,token);await Task.Delay(2200,token);await Job("refresh",null,token);
            var info=CloudFilesNative.Inspect(path);Require(info is not null&&info.Value.Info.InSyncState==1&&!CloudFilesNative.HasFullContent(path,first.Length),"入站新文件占位状态不正确。");
            Require((await File.ReadAllBytesAsync(path,token)).SequenceEqual(first),"入站水合内容不同。");
            var original=await _source!.GetMetadataAsync(relative,token);using var version=JsonDocument.Parse(original.Version);var tag=version.RootElement.GetProperty("ETag").GetString();
            await RawUpload(relative,second,tag,token);await Task.Delay(2200,token);await Job("refresh",null,token);
            Require((await File.ReadAllBytesAsync(path,token)).SequenceEqual(second),"入站更新刷新未取得新内容。");
            var metadata=await _source.GetMetadataAsync(relative,token);Require(await IndependentDigest(relative,metadata,token)==Digest(second),"入站更新独立摘要不同。");
            await RawDelete(relative,token);await Job("refresh",null,token);Require(!File.Exists(path),"入站删除后仍保留干净占位。");
        }
        private async Task MalformedPreview(CancellationToken token)
        {
            const string name="malformed-preview.png";Track("mount/"+name);var bytes=new byte[]{137,80,78,71,13,10,26,10,0,0,0,0};var path=Path.Combine(Local,name);
            // 先关闭预热，保证测试起点确为没有内容和缓存的冷占位；另行保留并发脱水的原始失败证据。
            await _manager!.SetThumbnailsEnabledAsync(_mount!.Id,false,token);
            await File.WriteAllBytesAsync(path,bytes,token);await Uploaded(name,bytes,token);await Job("freeSpace",[path],token);
            await _manager.SetThumbnailsEnabledAsync(_mount.Id,true,token);_manager.RequestPreview(path,128);await Task.Delay(2500,token);
            Require(_manager.GetPreview(path,128) is null&&!CloudFilesNative.HasFullContent(path,bytes.Length),"畸形媒体未安全回退。");
            await PreviewToggle(token);
        }
    }
}
