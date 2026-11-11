using System.Security.Principal;
using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.Recovery;

/// <summary>仅用于缺少旧身份但仍有云占位符的手动目录；意图先落盘，原生会话完成本地清理。</summary>
internal sealed class OrphanedDirectoryRecovery(string directory,Func<IReadOnlyList<MountDefinition>> managed,Action? ensureRunning=null,ICloudFilesRuntime? runtime=null)
{
    /// <summary>专用日志与旧移除日志分离，始终绑定当前SID、操作GUID和目录FileId。</summary>
    private sealed record Journal(int Schema,string Product,string Owner,OrphanedMountCandidate Root,string Stage,int Deleted=0,bool ContentCompleted=false,bool Completed=false,int? HResult=null,string? Error=null,string? FailedStage=null);
    private static readonly string[] Stages=["Confirmed","Registering","Registered","Connected","Clearing","ContentComplete","Completed","CleanupPending","Canceled","Failed"];
    private ICloudFilesRuntime Runtime=>runtime??NativeRuntimeClient.Get();
    private void ValidateManaged(string path)
    {
        ensureRunning?.Invoke();
        if(managed().Any(x=>MountPathGuard.IsUnder(x.LocalPath,path)||MountPathGuard.IsUnder(path,x.LocalPath)))throw new IOException("目录仍由现有配置管理，请使用该挂载的移除入口。");
    }
    private IEnumerable<Journal> Journals()
    {
        if(!Directory.Exists(directory))return [];
        return Directory.EnumerateFiles(directory,"*.json").Select(file=>
        {
            if(new FileInfo(file).Length>65536)throw new IOException("孤立目录恢复日志过大。");
            var value=JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(file),new JsonSerializerOptions{MaxDepth=16})??throw new IOException("孤立目录恢复日志为空。");
            if(value.Schema!=1||value.Product!=ApplicationPaths.ProductScope||value.Owner!=WindowsIdentity.GetCurrent().User!.Value||!value.Root.OrphanedDirectory||
                value.Root.MountId==Guid.Empty||!Stages.Contains(value.Stage)||Path.GetFileNameWithoutExtension(file)!=value.Root.MountId.ToString("N"))throw new IOException("孤立目录恢复日志身份或版本不匹配。");
            return value;
        }).ToArray();
    }
    /// <summary>还原设置只核验日志，不调用原生检查或重放清理；未知／损坏日志仍按原校验失败。</summary>
    internal void EnsureNoPendingForSettingsReset()
    {if(Journals().Any(value=>!value.Completed))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_4057AC6E04F7"));}
    private Journal? Pending(string path)
    {
        var matches=Journals().Where(x=>!x.Completed&&x.Root.LocalPath.Equals(path,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length>1)throw new IOException("同目录存在多个未完成恢复身份，请保留现场核对。");
        return matches.SingleOrDefault();
    }
    internal bool HasPending(string path)=>Pending(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) is not null;
    /// <summary>只读核验；普通目录拒绝，已有恢复意图允许显式继续处理剩余普通文件或注销收尾。</summary>
    internal async Task<OrphanedMountCandidate> InspectAsync(string path,CancellationToken token,OrphanedMountCandidate? expected=null)
    {
        token.ThrowIfCancellationRequested();var full=MountPathGuard.NormalizeRoot(path);ValidateManaged(full);
        var identity=MountPathGuard.FileIdentity(full);var journal=Pending(full);
        if(journal is not null&&journal.Root.RootIdentity!=identity)throw new IOException("孤立目录身份已变化，保留恢复日志。");
        var id=journal?.Root.MountId??expected?.MountId??Guid.NewGuid();
        if(expected is not null&&(expected.RootIdentity!=identity||expected.MountId!=id||!expected.OrphanedDirectory))throw new IOException("孤立目录确认快照已经变化。");
        var value=await Runtime.InvokeAsync("inspectOrphanedDirectory",new{path=full,rootIdentity=identity,operationId=id,resume=journal is not null},token:token).ConfigureAwait(false);
        return new(id,full,identity,"",value.GetProperty("registered").GetBoolean()){OrphanedDirectory=true};
    }
    /// <summary>启动只发现已有意图，不注册、连接或删除；身份异常显示为不可选择项。</summary>
    internal async Task<IReadOnlyList<OrphanedMountCandidate>> DiscoverAsync(CancellationToken token)
    {
        var result=new List<OrphanedMountCandidate>();
        foreach(var journal in Journals().Where(x=>!x.Completed))
        {
            token.ThrowIfCancellationRequested();
            try{result.Add(await InspectAsync(journal.Root.LocalPath,token,journal.Root).ConfigureAwait(false));}
            catch(OperationCanceledException){throw;}
            catch(Exception error){result.Add(journal.Root with{Error=error.Message});}
        }
        return result;
    }
    private void Save(Journal value)
    {
        Directory.CreateDirectory(directory);var file=Path.Combine(directory,value.Root.MountId.ToString("N")+".json");var temporary=file+".tmp";
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length>65536)throw new IOException("孤立目录恢复日志过大，停止操作。");
        try{using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}File.Move(temporary,file,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    /// <summary>确认后才持久保存意图并执行；安全检查点在取消后仍落盘，失败记录原阶段供显式重试。</summary>
    internal async Task<OrphanedMountResult> ClearAsync(OrphanedMountCandidate captured,CancellationToken token,Action<string>? progress=null)
    {
        if(!captured.CanRemove||!captured.OrphanedDirectory||captured.ShellOnly)throw new IOException("孤立目录确认快照不可处理。");
        var root=await InspectAsync(captured.LocalPath,token,captured).ConfigureAwait(false);
        var old=Pending(root.LocalPath);var journal=old??new(1,ApplicationPaths.ProductScope,WindowsIdentity.GetCurrent().User!.Value,root,"Confirmed");
        Save(journal);
        try
        {
            var result=await Runtime.InvokeAsync("clearOrphanedDirectory",new{path=root.LocalPath,rootIdentity=root.RootIdentity,operationId=root.MountId,resume=old is not null,contentCompleted=journal.ContentCompleted,deleted=journal.Deleted},
                (stage,data,cancel)=>
                {
                    cancel.ThrowIfCancellationRequested();if(stage!="orphanStage")throw new IOException("孤立目录检查点无效。");
                    var next=data.GetProperty("stage").GetString()!;if(!Stages.Contains(next))throw new IOException("孤立目录阶段无效。");
                    if(next is "Registering" or "Registered" or "Connected" or "Clearing")ValidateManaged(root.LocalPath);
                    var code=data.GetProperty("hresult").GetInt32();
                    journal=journal with{Stage=next,Deleted=data.GetProperty("deleted").GetInt32(),ContentCompleted=journal.ContentCompleted||data.GetProperty("contentCompleted").GetBoolean(),
                        FailedStage=data.TryGetProperty("failedStage",out var failed)?failed.GetString():null,Completed=next=="Completed"&&code>=0,HResult=code<0?code:null,Error=code<0?$"原生阶段未完成：0x{code:X8}":null};
                    Save(journal);progress?.Invoke(StageText(next,journal.Deleted));return Task.FromResult<object?>(new{saved=true});
                },token).ConfigureAwait(false);
            var code=result.GetProperty("hresult").GetInt32();var errors=new List<string>();
            foreach(var error in result.GetProperty("errors").EnumerateArray())errors.Add(error.GetProperty("path").GetString()+$"：0x{error.GetProperty("hresult").GetInt32():X8}");
            var failureCount=result.GetProperty("failures").GetInt32();if(failureCount>errors.Count)errors.Add($"另有 {failureCount-errors.Count} 项失败，详细阶段见恢复日志。");
            if(code<0)errors.Add($"处理未完成：0x{code:X8}");
            return new(result.GetProperty("completed").GetBoolean(),result.GetProperty("deleted").GetInt32(),(code<0&&result.TryGetProperty("failedStage",out var failed)&&failed.GetString() is {Length:>0} actual?actual:result.GetProperty("stage").GetString()!),errors){HResult=code<0?code:null};
        }
        catch(Exception error)
        {
            journal=journal with{Completed=false,HResult=OrphanedMountBatch.Code(error),Error=error is OperationCanceledException?"已取消，已完成的删除不回滚。":error.Message};Save(journal);throw;
        }
    }
    private static string StageText(string stage,int deleted)=>stage switch
    {
        "Registering"=>"正在临时注册本地恢复提供程序…","Registered"=>"临时注册已核验，准备连接…","Connected"=>"本地恢复提供程序已连接。",
        "Clearing"=>$"正在清空本地内容，已删除 {deleted} 项…","ContentComplete"=>"内容已清空，正在断开并注销…","Completed"=>"已清空并注销，空根目录已保留。",
        "CleanupPending"=>"临时注册收尾未完成，恢复记录已保留。","Canceled"=>"已取消，临时连接与注册已尝试清理。",_=>"处理未完成，恢复记录已保留。"
    };
}
/// <summary>仅表示缺少旧根或精确入口证据；权限或API错误不得归入此类型。</summary>
internal sealed class MissingOrphanedMountEvidenceException():IOException("目录没有可核验的原生根身份或每根Shell入口。");
