using System.Security.Cryptography;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>实例级确定性测试屏障；生产默认关闭，不经 UI/IPC 配置。提交阶段的回调不得获取根锁。</summary>
    internal Func<string,LocalUpload,Task>? UploadCheckpointForTests { get; set; }
    private Task UploadCheckpointAsync(string stage,LocalUpload item)=>UploadCheckpointForTests?.Invoke(stage,item)??Task.CompletedTask;
    /// <summary>网络发送前短暂打开只读共享的文件，复制到挂载根外并核对身份、USN 和摘要。</summary>
    private async Task<LocalUpload?> PrepareUploadAsync(Runtime root,LocalUpload queued,CancellationToken token)
    {
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        string? created=null;
        try
        {
            if(root.RemovalRequested||root.Definition.RemovalPending||root.Session is null||root.Source is null)throw new OperationCanceledException(token);
            var item=_repository.Upload(queued.Id);if(item is null||item.CancelRequested)return null;
            if(item.State is not("Queued" or "Waiting" or "Prepared")||item.Error==CloudUploadState.TypeConflictMessage)return null;
            var path=RelativePaths.ToLocal(root.Definition.LocalPath,item.LocalPath);MountPathGuard.ValidateOperation(root.Definition,path);
            var stamp=LocalFileState.Read(path);CloudUploadState.ValidateTypes(item,stamp);
            if(item.SnapshotPath is not null&&File.Exists(item.SnapshotPath)&&item.SnapshotStamp==stamp.Key&&item.SnapshotGeneration==item.Generation)
                return ChangeUpload(item.Id,x=>x with{State="Prepared",LastCheckedUtc=null});
            var info=CloudFilesNative.Inspect(path);
            if(!stamp.IsDirectory&&info is not null&&!CloudFilesNative.HasFullContent(path,stamp.Length))
            {
                var entry=_repository.Entries(item.MountId).FirstOrDefault(x=>x.Id==item.EntryId);
                if(entry is null)throw new SourceConflictException("部分占位缺少原始映射，无法安全补齐。");
                var metadata=await root.Source.GetMetadataAsync(entry.SourcePath,token).ConfigureAwait(false);
                if(!root.Source.IsSameVersion(metadata.Version,item.ExpectedVersion??entry.Version))throw new SourceConflictException("远端版本变化，无法补齐本地修改过的部分占位；保留文件。");
                using(var priority=root.Session.PrioritizeDownload(entry.Id))
                using(var handle=CloudFilesNative.Open(path))await CloudFilesNative.HydrateAsync(handle,token).ConfigureAwait(false);
                if(!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))throw new SourceConflictException("修改过的占位尚未完整补齐，未开始上传。");
                stamp=LocalFileState.Read(path);CloudUploadState.ValidateTypes(item,stamp);
            }
            var directory=Path.Combine(_repository.DataDirectory,"UploadStaging",item.MountId.ToString("N"));Directory.CreateDirectory(directory);
            string? digest=null;
            if(!stamp.IsDirectory)
            {
                created=Path.Combine(directory,Guid.NewGuid().ToString("N")+".snapshot");
                await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
                var before=LocalFileState.Read(input.SafeFileHandle);CloudUploadState.ValidateTypes(item,before);
                if(before.Identity!=stamp.Identity||before.Key!=stamp.Key)throw new IOException("文件在准备快照时变化，等待稳定后重试。");
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using(var output=new FileStream(created,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.Asynchronous|FileOptions.SequentialScan))
                {
                    var buffer=new byte[131072];int count;
                    while((count=await input.ReadAsync(buffer,token).ConfigureAwait(false))!=0){hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false);}
                    await output.FlushAsync(token).ConfigureAwait(false);output.Flush(true);
                }
                if(LocalFileState.Read(input.SafeFileHandle).Key!=before.Key||LocalFileState.Read(path).Key!=before.Key)throw new IOException("文件在复制时变化，等待稳定后重试。");
                digest=Convert.ToHexString(hash.GetHashAndReset());stamp=before;
            }
            var previous=item.SnapshotPath;
            var prepared=ChangeUpload(item.Id,x=>x with{State="Prepared",SnapshotPath=created,SnapshotSha256=digest,SnapshotStamp=stamp.Key,SnapshotIdentity=stamp.Identity,SnapshotGeneration=x.Generation,Total=stamp.IsDirectory?0:stamp.Length,Bytes=0,Error=null,LastCheckedUtc=null,
                BaselineSha256=x.BaselineSha256??(x.ExpectedVersion is null?null:_repository.Baseline(x.MountId,x.SourcePath,x.ExpectedVersion))});
            created=null;DeleteUploadSnapshot(previous);return prepared;
        }
        finally{DeleteUploadSnapshot(created);root.Gate.Release();}
    }
    /// <summary>远端已确认后在根锁内提交；文件改变则只更新远端基线，保留新一代本地保护。</summary>
    private async Task CommitUploadAsync(Runtime root,LocalUpload prepared,CancellationToken token)
    {
        var receipt=prepared.Receipt??throw new IOException("上传回执缺失。");
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(root.RemovalRequested||root.Definition.RemovalPending)throw new OperationCanceledException(token);
            MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
            if(DeletedSourceAncestor(_repository.Entries(root.Definition.Id),prepared.SourcePath) is not null)
            {ChangeUpload(prepared.Id,current=>current with{State="Conflict",Error=RemoteDeletedMessage,LastCheckedUtc=null});return;}
            var path=RelativePaths.ToLocal(root.Definition.LocalPath,prepared.LocalPath);var exists=!LocalMissing(path);LocalFileStamp? committedStamp=null;
            CloudUploadState.ValidateTypes(prepared,remoteDirectory:receipt.IsDirectory);
            if(_repository.Upload(prepared.Id)?.Error==CloudUploadState.TypeConflictMessage)throw new SourceConflictException(CloudUploadState.TypeConflictMessage);
            // 远端 StableId 可为空且不保证可用；占位身份始终由本产品维护。
            var mapped=_repository.Entries(prepared.MountId).FirstOrDefault(x=>!x.Deleted&&x.SourcePath==prepared.SourcePath&&x.LocalPath==prepared.LocalPath);
            var entry=new MountEntry(mapped?.Id??prepared.Id,prepared.SourcePath,prepared.LocalPath,receipt.Version,receipt.IsDirectory,Materialized:true);
            if(exists&&!prepared.IsCopy)
            {
                MountPathGuard.ValidateOperation(root.Definition,path);
                CloudUploadState.ValidateTypes(prepared,LocalFileState.Read(path),receipt.IsDirectory);
                var current=_repository.Upload(prepared.Id);
                if(current?.Generation==prepared.SnapshotGeneration)committedStamp=CloudUploadState.Commit(root.Definition,entry,prepared);
            }
            if(committedStamp is not null)await UploadCheckpointAsync("CommitVerified",prepared).ConfigureAwait(false);
            var completed=_repository.CommitUploadedEntry(prepared,entry,receipt,committedStamp,!exists);
            CompleteOwnUploadEcho(root,receipt);
            await UploadCheckpointAsync("Committed",completed).ConfigureAwait(false);
            if(committedStamp is not null&&exists)_previews.Queue(root.Definition,entry);
            PublishUpload(completed);DeleteUploadSnapshot(prepared.SnapshotPath);root.Uploads?.Wake();
        }
        finally{root.Gate.Release();}
    }
    /// <summary>恢复发送只接受本挂载的暂存文件，不沿快照目录中的联接或符号链接读取。</summary>
    private void ValidateUploadSnapshot(LocalUpload item)
    {
        var staging=Path.Combine(_repository.DataDirectory,"UploadStaging");var own=Path.Combine(staging,item.MountId.ToString("N"));
        var path=item.SnapshotPath;
        if(path is null||!Path.IsPathFullyQualified(path)||!MountPathGuard.IsUnder(own,path)||!Path.GetExtension(path).Equals(".snapshot",StringComparison.OrdinalIgnoreCase))
            throw new SourceConflictException("上传快照路径不属于本挂载，已拒绝读取。");
        for(var cursor=path;;cursor=Path.GetDirectoryName(cursor)!)
        {
            if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new SourceConflictException("上传快照路径包含外部链接，已拒绝读取。");
            if(cursor.Equals(staging,StringComparison.OrdinalIgnoreCase))break;
        }
    }
    /// <summary>只清理本程序根外快照目录中的文件；未知结果仍引用的快照由持久记录保留。</summary>
    private void DeleteUploadSnapshot(string? path)
    {
        if(path is null||!MountPathGuard.IsUnder(Path.Combine(_repository.DataDirectory,"UploadStaging"),path))return;
        try
        {
            var staging=Path.Combine(_repository.DataDirectory,"UploadStaging");
            for(var cursor=Path.GetFullPath(path);;cursor=Path.GetDirectoryName(cursor)!)
            {
                if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)return;
                if(cursor.Equals(staging,StringComparison.OrdinalIgnoreCase))break;
            }
            File.Delete(path);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
