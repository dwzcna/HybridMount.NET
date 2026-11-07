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
        text.Append("预览队列：").AppendLine(_previews.Describe(root.Definition.Id));
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
                MountPathGuard.ValidateOperation(root.Definition,path);var native=CloudFilesNative.Inspect(path);
                text.AppendLine(native is {} value?$"CFAPI：InSync={value.Info.InSyncState}；ModifiedBytes={value.Info.ModifiedSize}；Pin={value.Info.PinState}；OnDiskBytes={value.Info.OnDiskSize}":"CFAPI：非占位或不存在");
                if(native is {} pending&&pending.Info.PinState==2&&pending.Info.OnDiskSize>0)text.AppendLine("释放空间意图：UNPINNED，仍有本地缓存；等待无修改、无占用及无未决工作的安全脱水。");
                text.AppendLine(CloudShellState.Read(path).ToString());
            }
            catch(Exception error){text.Append("只读查询失败：").AppendLine(error.GetType().Name);}
        }
        return text.ToString();
    },cancel),token);
}
