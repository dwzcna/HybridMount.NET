using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>调用者持有根锁；只留完整本地且干净的图片，最多等待两秒。失败或期限到仍执行原释放，不触碰远端。</summary>
    private async Task PreserveBeforeDehydrateAsync(Runtime root,MountEntry entry,CancellationToken token)
    {
        if(!CustomPreviewsAvailable||!root.Definition.ThumbnailsEnabled||entry.IsDirectory||!PreviewWorker.Supports(entry.LocalPath)||root.Session is null)return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));var started=System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if(_previews.HasFrames(root.Definition,entry))return;
            var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);
            if(!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))return;
            var result=await root.Session.Runtime.InvokeAsync("renderLocalThumbnail",root.Session.EntryArgs(entry),token:deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();var frames=System.Text.Json.JsonSerializer.Deserialize<Dictionary<int,byte[]>>(result.GetProperty("frames"));
            if(frames is not null&&_previews.PreserveLocal(root.Definition,entry,NativeRuntimeArguments.Stamp(result),frames))_previews.RecordOutcome(root.Definition.Id,"脱水前留图已发布");
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){_previews.RecordOutcome(root.Definition.Id,"脱水前留图超时，继续释放");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or Microsoft.Data.Sqlite.SqliteException)
        {_previews.RecordOutcome(root.Definition.Id,"脱水前留图失败，继续释放",error.HResult,true);}
        finally{_previews.RecordTiming(root.Definition.Id,"脱水前留图",(long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);}
    }
}
