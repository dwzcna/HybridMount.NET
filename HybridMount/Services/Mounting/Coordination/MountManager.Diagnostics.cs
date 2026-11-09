using System.Text;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>应用拥有按需诊断；只读包含全部未解决上传问题，再采样三个已知条目，不水合、不联网、不改变当前页面选择。</summary>
    internal Task<string> ReadDiagnosticsAsync(Guid id,CancellationToken token=default)=>OwnAsync(cancel=>Task.Run(()=>
    {
        cancel.ThrowIfCancellationRequested();var root=Root(id);var snapshot=Snapshot(root);
        var text=new StringBuilder(CloudShellState.EnvironmentDescription()).AppendLine().Append("挂载：").Append(snapshot.Definition.Name)
            .Append("；连接：").Append(snapshot.IsConnected?"已连接":"已断开").Append("；状态：").AppendLine(snapshot.Status);
        if(snapshot.Detail.Length>0)text.AppendLine(snapshot.Detail);
        text.AppendLine(CloudShellState.RootDescription(root.Definition));
        try
        {
            if(RemovalCompletion().Read(id) is {} removal)
            {
                text.Append("移除阶段：").AppendLine(removal.Description).Append("最近失败阶段：").AppendLine(removal.FailedStage??"无")
                    .Append("HRESULT：").AppendLine(removal.HResult is {} result?$"0x{result:X8}":"无")
                    .Append("移除错误：").AppendLine(removal.Error??"无").Append("最近包外核验：").AppendLine(removal.ExternalVerified?"无残留":"尚未确认完成");
                var remaining=(RemovalShellForTests??_removalShell).Verify(removal.Root,removal.Shell);
                text.Append("当前真实视图及包内残项：").AppendLine(remaining.Length==0?"无":string.Join("；",remaining));
            }
        }
        catch(Exception error){text.Append("移除注册只读核验失败：").Append(error.Message).Append("；HRESULT=0x").AppendLine(error.HResult.ToString("X8"));}
        foreach(var resolution in _repository.Uploads(id).Where(x=>x.Resolution is not null||x.ConflictRecoveryPath is not null))
            text.Append("冲突取舍：").Append(resolution.LocalPath).Append("；阶段：").Append(resolution.Resolution?.Phase??"已完成交接").Append("；备份：").AppendLine(resolution.Resolution?.BackupPath??resolution.ConflictRecoveryPath);
        text.AppendLine(FileExclusionStatus(id));
        foreach(var rename in RenameIntents(id).Where(x=>!x.Finished))text.Append("改名排除收尾：").Append(rename.OldLocalPath).Append(" → ").Append(rename.LocalPath).Append("；远端旧路径：").Append(rename.SourcePath).Append(" → ").Append(rename.DestinationPath??rename.LocalPath).Append("；移动已确认：").Append(rename.RemoteMoved).Append("；阶段：").Append(rename.StageText).Append("；文件身份：").Append(rename.FileId).Append("；本地已独立：").Append(rename.LocalDetached).Append("；").AppendLine(rename.Error??"无错误");
        if(_renameErrors.TryGetValue(id,out var journalError))text.Append("改名日志错误：").AppendLine(journalError.Error);
        text.AppendLine(snapshot.Capacity?.Display??snapshot.CapacityDisplay);
        text.Append("缩略图库：").AppendLine(ThumbnailCachePaths.ForMount(PreviewDirectory(_repository),id));
        text.Append("预览队列：").AppendLine(_previews.Describe(root.Definition.Id));
        text.Append("目录观察：").AppendLine(root.Definition.ThumbnailsEnabled?root.Session?.PreviewObservationDescription??"未运行":"已关闭");
        text.Append("挂载缩略图开关：").AppendLine(root.Definition.ThumbnailsEnabled?"已开启":"已关闭");
        text.Append("远端检测模式：").AppendLine(snapshot.Detection?.Mode??"尚未检测");
        if(root.ChangeSession.Fallback is {} fallback)
            text.Append("RFC 6578 本次会话已停用：").AppendLine(fallback.Reason).Append("降级时间：").AppendLine(fallback.OccurredUtc.ToLocalTime().ToString("O"));
        if(root.Source is IMountSourceRuntime {ConnectionDiagnostics:{} capabilities})text.AppendLine(capabilities);
        var allUploads=_repository.Uploads(id);var uploads=allUploads.GroupBy(item=>item.LocalPath,StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.OrderByDescending(item=>item.ChangedUtc).First(),StringComparer.Ordinal);
        var entries=_repository.Entries(id).Where(e=>!e.Deleted).ToDictionary(e=>e.LocalPath,StringComparer.Ordinal);
        var paths=allUploads.Where(UploadAttentionRules.NeedsAttention).Select(x=>x.LocalPath).Concat(entries.Keys.OrderBy(x=>x,StringComparer.Ordinal).Take(3)).Distinct(StringComparer.Ordinal);
        foreach(var relative in paths)
        {
            entries.TryGetValue(relative,out var entry);uploads.TryGetValue(relative,out var upload);
            cancel.ThrowIfCancellationRequested();var path=RelativePaths.ToLocal(root.Definition.LocalPath,relative);
            text.AppendLine().Append("条目：").AppendLine(relative);
            text.Append("上传：").Append(upload?.State??"无记录").Append("；索引保护：").Append(entry?.Protected.ToString()??"尚未建立索引").AppendLine();
            foreach(var problem in allUploads.Where(x=>x.LocalPath==relative&&UploadAttentionRules.NeedsAttention(x)))
                if(problem.Attention is {} issue)
                    text.Append("问题：").Append(issue.Reason).Append("；阶段：").Append(issue.Stage).Append("；代次：").Append(issue.Generation).Append("；时间：").AppendLine(issue.OccurredUtc.ToLocalTime().ToString("O"))
                        .Append("Explorer提示：").Append(issue.ShellState).Append("；").AppendLine(issue.ShellError);
            try
            {
                if(root.Definition.RemovalPending&&LocalFileMetadata.TryRead(path) is null){text.AppendLine("本地已删除");continue;}
                MountPathGuard.ValidateOperation(root.Definition,path);var native=CloudFilesNative.Inspect(path);
                text.AppendLine(native is {} value?$"CFAPI：InSync={value.Info.InSyncState}；ModifiedBytes={value.Info.ModifiedSize}；Pin={value.Info.PinState}；OnDiskBytes={value.Info.OnDiskSize}":"CFAPI：非占位或不存在");
                if(native is {} pending&&pending.Info.PinState==2&&pending.Info.OnDiskSize>0)text.AppendLine("释放空间意图：UNPINNED，仍有本地缓存；等待无修改、无占用及无未决工作的安全脱水。");
                text.AppendLine(CloudShellState.Read(path).ToString());
            }
            catch(Exception error){text.Append("只读查询失败：").Append(error.GetType().Name).Append("；").Append(error.Message).Append("；HRESULT=0x").AppendLine(error.HResult.ToString("X8"));}
        }
        return text.ToString();
    },cancel),token);
}
