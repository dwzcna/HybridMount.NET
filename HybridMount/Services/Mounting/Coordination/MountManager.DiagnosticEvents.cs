using HybridMount.Models.Diagnostics;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly object _diagnosticGate=new();
    private readonly Dictionary<string,(string Signature,bool Failed)> _diagnosticStates=[];
    /// <summary>有界状态转换去重；不记录进度、不输出路径或原始错误，只保留任务／挂载与安全异常代码。</summary>
    private void RecordStateDiagnostic(string area,Guid mount,string id,string stage,bool failed,string version,Exception? error=null,string? description=null)
    {
        var key=area+":"+mount.ToString("N")+":"+id;var recovery=false;
        lock(_diagnosticGate)
        {
            _diagnosticStates.TryGetValue(key,out var previous);
            var signature=stage+":"+version;
            if(failed){if(previous.Signature==signature&&previous.Failed)return;}
            else{if(!previous.Failed||stage!="Completed")return;recovery=true;}
            if(_diagnosticStates.Count>=2048&&!_diagnosticStates.ContainsKey(key))_diagnosticStates.Remove(_diagnosticStates.Keys.First());
            _diagnosticStates[key]=(signature,!recovery);
        }
        var context=(description??(recovery?"问题已完成收尾":"操作尚未完成，请在任务页核对"))+$"；阶段={SafeDiagnostic.Field(stage)}；挂载={mount:N}；任务={SafeDiagnostic.Field(id)}；事件={SafeDiagnostic.Field(version)}";
        var severity=recovery?"Information":stage is "Failed" or "ResolutionPending" or "RemoteCommitted"?"Error":"Warning";
        if(error is null)_logger.Queue(area,severity,context);else _logger.QueueException(area,severity,context,error);
    }
    private static string DiagnosticVersion(string? value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value??"")))[..16];
    /// <summary>页面只补服务尚未记录的异常；日志服务按异常实例去重，不改任务或授权。</summary>
    internal void ReportDiagnosticException(string area,string context,Exception error)
    {if(error is not OperationCanceledException)_logger.QueueException(area,"Error",context,error);}
    private void RecordUploadDiagnostic(Models.Mounting.LocalUpload item,Exception? error)
    {
        var failed=item.State is not("Excluded" or "Canceled" or "Ignored" or "Completed")&&
            (Models.Mounting.UploadAttentionRules.IsFailureState(item)||!string.IsNullOrEmpty(item.Error));
        RecordStateDiagnostic("上传",item.MountId,item.Id,item.State,failed,item.Generation+":"+(item.Attention?.NotificationKey??"")+":"+DiagnosticVersion(item.Error),error);
    }
}
