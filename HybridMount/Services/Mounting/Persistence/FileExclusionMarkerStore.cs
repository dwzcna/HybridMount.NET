using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;
namespace HybridMount.Services.Mounting;

/// <summary>原生排除标记的身份日志；每挂载独立文件，原子落盘后才改属性，不存正文或凭据。</summary>
internal sealed class FileExclusionMarkerStore(string directory, Guid mountId)
{
    internal sealed record Mark(string Path,string Identity,uint PreviousPin,string Phase,LocalFileStamp Before,DateTimeOffset AttemptUtc,string? Error=null,uint? OwnedPin=null);
    internal sealed record Result(LocalFileStamp? Before,LocalFileStamp? After,bool Changed,Exception? Failure=null);
    private readonly string _path=Path.Combine(directory,"ExclusionMarkers",mountId.ToString("N")+".json");
    private readonly object _gate=new();
    private Dictionary<string,Mark>? _marks;
    private bool _definitionReady;
#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>仅测试检查点故障注入；参数句柄无效，不跨进程借出文件。</summary>
    internal Func<SafeFileHandle,uint,int>? SetPinForTests {get;set;}
#endif
    private Dictionary<string,Mark> Load()
    {
        if(_marks is not null)return _marks;
        try
        {
            var loaded=File.Exists(_path)?JsonSerializer.Deserialize<Dictionary<string,Mark>>(File.ReadAllText(_path))??throw new IOException("排除标记日志为空。"):[];
            if(loaded.Any(x=>x.Value is null||x.Key!=x.Value.Identity||x.Value.Before is null||x.Value.Before.Identity!=x.Key||string.IsNullOrEmpty(x.Value.Path)||
                x.Value.PreviousPin is not(0 or 1 or 2 or 3 or 4)||x.Value.OwnedPin is not(null or 0 or 2 or 3)||x.Value.Phase is not("Applied" or "NativeDeferred" or "ApplyPending" or "RestorePending")))throw new IOException("排除标记日志字段无效。");
            foreach(var mark in loaded.Values)RelativePaths.Normalize(mark.Path);
            return _marks=loaded;
        }
        catch(Exception error)when(error is JsonException or ArgumentException){throw new IOException("排除标记日志损坏，未改写原记录；上传排除仍生效。",error);}
    }
    internal bool HasMarks{get{lock(_gate)return Load().Count!=0;}}
    /// <summary>升级只读取已存在的精确排除证据；新规则保存不能凭文件名生成历史改名授权。</summary>
    internal Mark? ExistingMark(string identity,string relative){lock(_gate)return Load().GetValueOrDefault(identity) is {} mark&&mark.Path==relative?mark:null;}
    /// <summary>完整扫描后只丢弃确认已消失或被替换的日志；不访问越界路径、不修改文件。</summary>
    internal void Prune(MountDefinition definition)
    {
        lock(_gate)
        {
            var changed=false;
            foreach(var item in Load().Values.ToArray())
            {
                var path=RelativePaths.ToLocal(definition.LocalPath,item.Path);
                if((!MountPathGuard.TryValidateOperation(definition,path)||MountPathGuard.FileIdentity(path)!=item.Identity)&&IdentityDeleted(definition,item.Identity))
                    changed|=_marks!.Remove(item.Identity);
            }
            if(changed)Save();
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct FileIdDescriptor{public uint Size,Type;public long FileId,Reserved;}
    /// <summary>路径消失可能只是改名；用同卷NTFS文件ID确认删除后才丢弃恢复日志。只取元数据句柄，不解析正文或对未知路径执行CFAPI。</summary>
    private static bool IdentityDeleted(MountDefinition definition,string identity)
    {
        MountPathGuard.ValidateOperation(definition,definition.LocalPath);
        using var root=CloudFilesNative.CreateFile(definition.LocalPath,0x80,7,0,3,0x02200000,0);
        if(root.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        if(identity.Length!=48||identity.Any(c=>!Uri.IsHexDigit(c)))throw new IOException("排除标记文件身份无效，保留日志。");
        var bytes=Convert.FromHexString(identity);var rootBytes=Convert.FromHexString(LocalFileState.Read(root).Identity);
        if(bytes.Length!=24||!bytes.AsSpan(0,8).SequenceEqual(rootBytes.AsSpan(0,8)))throw new IOException("排除标记文件身份的卷信息无效，保留日志。");
        // NTFS的64位文件ID含复用序号；不把未知的128位形式或原生查询错误猜测为已删除。
        if(BitConverter.ToInt64(bytes,16)!=0)return false;
        var descriptor=new FileIdDescriptor{Size=24,FileId=BitConverter.ToInt64(bytes,8)};
        using var existing=OpenFileById(root,in descriptor,0x80,7,0,0x02200000);
        if(!existing.IsInvalid)return false;
        var error=Marshal.GetLastWin32Error();if(error==2)return true;
        // 本机NTFS对已回收ID也返回INVALID_PARAMETER；不把模糊结果当作删除证明，保留日志供后来同ID路径核对。
        if(error==87)return false;
        throw new Win32Exception(error,$"按文件身份核对删除状态失败，保留排除恢复日志；Win32={error}：{new Win32Exception(error).Message}");
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint,in FileIdDescriptor descriptor,uint access,uint share,nint security,uint flags);
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var temporary=_path+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,_marks);stream.Flush(true);}
            File.Move(temporary,_path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    /// <summary>仅返回已保存的诊断快照，不查询挂载文件、不联网；日志损坏明确报告。</summary>
    internal string Describe()
    {
        lock(_gate)
        {
            var marks=Load();var errors=marks.Values.Where(x=>x.Error is not null).ToArray();
            return $"排除标记：已记录 {marks.Count}；待完成 {marks.Values.Count(x=>x.Phase is not("Applied" or "NativeDeferred"))}"+
                $"；原生标记等待完整内容 {marks.Values.Count(x=>x.Phase=="NativeDeferred")}"+
                string.Concat(errors.Select(x=>$"\n{x.Path}：{x.Error}"));
        }
    }
    /// <summary>仅后台扫描调用并持根锁；属性保护内验证当前根与文件身份，先写日志再设置单文件PinState及状态图标，不读正文。</summary>
    internal Result Reconcile(MountDefinition definition,string relative,bool excluded,CancellationToken token=default)
    {
        lock(_gate)
        {
            token.ThrowIfCancellationRequested();if(definition.Id!=mountId||definition.RemovalPending)throw new IOException("排除标记的挂载身份已变化。");
            var marks=Load();if(!excluded&&marks.Count==0)return new(null,null,false);
            if(excluded&&!_definitionReady){CloudRootRegistration.EnsureExclusionDefinitionAsync(definition,token).GetAwaiter().GetResult();_definitionReady=true;}
            var path=RelativePaths.ToLocal(definition.LocalPath,relative);Mark? record=null,prior=null;LocalFileStamp? before=null,after=null;var changed=false;var completed=false;
            try
            {
                NativeRuntimeClient.Get().InvokeAsync("applyExclusion",NativeRuntimeArguments.Root(definition,new{path,excluded,text=excluded?CloudItemStateProperty.ExclusionText:"",icon=excluded?CloudItemStateProperty.ExclusionIcon:""}),(stage,data,cancel)=>
                {
                    cancel.ThrowIfCancellationRequested();
                    if(stage is "exclusionProbe" or "exclusionGuarded")
                    {
                        before=NativeRuntimeArguments.Stamp(data);marks.TryGetValue(before.Identity,out prior);var pin=data.GetProperty("pinState").GetUInt32();var deferred=data.GetProperty("deferred").GetBoolean();
                        if(!excluded&&prior is null)return Task.FromResult<object?>(new{proceed=false});
                        if(prior?.Error is not null&&prior.Path==relative&&prior.Phase==(excluded?"ApplyPending":"RestorePending")&&prior.AttemptUtc.AddSeconds(30)>DateTimeOffset.UtcNow)return Task.FromResult<object?>(new{proceed=false});
                        var desired=excluded?(deferred?DeferredPin(prior?.PreviousPin??pin):CloudFilesNative.PinStateExcluded):prior!.PreviousPin;
                        record=new(relative,before.Identity,prior?.PreviousPin??pin,excluded?"ApplyPending":"RestorePending",before,DateTimeOffset.UtcNow,OwnedPin:excluded?desired:prior?.OwnedPin??(prior?.Phase=="NativeDeferred"?DeferredPin(prior.PreviousPin):CloudFilesNative.PinStateExcluded));
                        if(excluded&&pin==desired&&prior is {Error:null}&&prior.Phase==(deferred?"NativeDeferred":"Applied")&&prior.Path==relative&&!CloudItemStateProperty.Plan(definition,path,2,CloudItemStateProperty.ExclusionText,CloudItemStateProperty.ExclusionIcon).Changed){record=null;return Task.FromResult<object?>(new{proceed=false});}
                        if(stage=="exclusionProbe"){record=null;return Task.FromResult<object?>(new{proceed=true});}
                        cancel.ThrowIfCancellationRequested();marks[before.Identity]=record;Save();var changePin=excluded?pin!=desired:pin==record.OwnedPin&&record.PreviousPin!=pin;
#if HYBRIDMOUNT_HEADLESS_TEST
                        if(changePin&&SetPinForTests is not null){using var invalid=new SafeFileHandle(-1,false);CloudFilesNative.Check(SetPinForTests(invalid,desired));}
#endif
                        return Task.FromResult<object?>(new{proceed=true,desired,changePin});
                    }
                    if(stage=="exclusionBegin")return Task.FromResult<object?>(null);
                    if(stage is "exclusionCommit" or "exclusionProof")
                    {
                        after=NativeRuntimeArguments.Stamp(data);changed=data.GetProperty("changed").GetBoolean();if(stage=="exclusionCommit")
                        {
                            if(record is null)throw new IOException("排除标记缺少恢复记录。");
                            cancel.ThrowIfCancellationRequested();
                            if(excluded)marks[record.Identity]=record with{Phase=record.OwnedPin==CloudFilesNative.PinStateExcluded?"Applied":"NativeDeferred"};else marks.Remove(record.Identity);Save();completed=true;
                        }
                        return Task.FromResult<object?>(null);
                    }
                    throw new IOException("原生排除阶段无效。");
                },token).GetAwaiter().GetResult();
                return new(prior?.Before??before,after??before,changed);
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or Win32Exception or COMException or OperationCanceledException)
            {
                if(record is not null&&!completed){marks[record.Identity]=record with{Error=$"原生排除标记未完成；HRESULT=0x{error.HResult:X8}"};Save();}
                if(changed&&before is not null&&after is not null)return new(before,after,true,error);throw;
            }
        }
    }
    /// <summary>暂缓期间撤销固定等待；解除规则时按身份恢复，不触发正文下载。</summary>
    private static uint DeferredPin(uint previous)=>previous==2?2u:0u;
}
