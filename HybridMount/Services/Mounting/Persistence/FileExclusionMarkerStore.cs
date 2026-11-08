using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;
namespace HybridMount.Services.Mounting;

/// <summary>原生排除标记的身份日志；每挂载独立文件，原子落盘后才改属性，不存正文或凭据。</summary>
internal sealed class FileExclusionMarkerStore(string directory, Guid mountId)
{
    internal sealed record Mark(string Path,string Identity,uint PreviousPin,string Phase,LocalFileStamp Before,DateTimeOffset AttemptUtc,string? Error=null);
    internal sealed record Result(LocalFileStamp? Before,LocalFileStamp? After,bool Changed);
    private readonly string _path=Path.Combine(directory,"ExclusionMarkers",mountId.ToString("N")+".json");
    private readonly object _gate=new();
    private Dictionary<string,Mark>? _marks;
    private bool _definitionReady;
    /// <summary>实例级故障注入；生产始终调用原有CfSetPinState封装。</summary>
    internal Func<SafeFileHandle,uint,int>? SetPinForTests {get;set;}
    private Dictionary<string,Mark> Load()
    {
        if(_marks is not null)return _marks;
        try
        {
            var loaded=File.Exists(_path)?JsonSerializer.Deserialize<Dictionary<string,Mark>>(File.ReadAllText(_path))??throw new IOException("排除标记日志为空。"):[];
            if(loaded.Any(x=>x.Value is null||x.Key!=x.Value.Identity||x.Value.Before is null||x.Value.Before.Identity!=x.Key||string.IsNullOrEmpty(x.Value.Path)||
                x.Value.PreviousPin is not(0 or 1 or 2 or 3 or 4)||x.Value.Phase is not("Applied" or "ApplyPending" or "RestorePending")))throw new IOException("排除标记日志字段无效。");
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
            return $"排除标记：已记录 {marks.Count}；待完成 {marks.Values.Count(x=>x.Phase!="Applied")}"+
                string.Concat(errors.Select(x=>$"\n{x.Path}：{x.Error}"));
        }
    }
    /// <summary>仅后台扫描调用并持根锁；属性保护内验证当前根与文件身份，先写日志再设置单文件PinState及状态图标，不读正文。</summary>
    internal Result Reconcile(MountDefinition definition,string relative,bool excluded,CancellationToken token=default)
    {
        lock(_gate)
        {
            token.ThrowIfCancellationRequested();
            if(definition.Id!=mountId||definition.RemovalPending)throw new IOException("排除标记的挂载身份已变化。");
            var marks=Load();if(!excluded&&marks.Count==0)return new(null,null,false);
            var path=RelativePaths.ToLocal(definition.LocalPath,relative);MountPathGuard.ValidateOperation(definition,path);
            // 已完成标记只用共享属性读句柄检查，避免周期扫描阻挡正常打开和改名。
            using var probe=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);
            if(probe.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            var before=LocalFileState.Read(probe);if(before.IsDirectory)return new(before,before,false);
            var info=CloudFilesNative.Inspect(probe);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(info is not null&&marker?.MountId!=mountId)throw new IOException("排除标记拒绝操作其他提供程序的占位。");
            if(MountPathGuard.FileIdentity(path)!=before.Identity)throw new IOException("排除标记文件身份已变化。");
            marks.TryGetValue(before.Identity,out var prior);
            if(!excluded&&prior is null)return new(before,before,false);
            var pin=ReadPin(probe,info?.Info.PinState);
            var text=excluded?CloudItemStateProperty.ExclusionText:"";var icon=excluded?CloudItemStateProperty.ExclusionIcon:"";
            if(prior?.Error is not null&&prior.Path==relative&&prior.Phase==(excluded?"ApplyPending":"RestorePending")&&prior.AttemptUtc.AddSeconds(30)>DateTimeOffset.UtcNow)
                return new(prior.Before,before,false);
            if(excluded&&pin==CloudFilesNative.PinStateExcluded&&prior is {Phase:"Applied",Error:null}&&prior.Path==relative&&
                !CloudItemStateProperty.Plan(definition,path,CloudItemStateProperty.ExclusionId,text,icon).Changed)
                return new(prior.Before,before,false);
            if(!CloudRootRegistration.IsOwnedRoot(definition))throw new IOException("原生根身份无法确认，排除规则生效但系统标记未执行。");
            // 只有实际修改时请求共享保护；DELETE权限只用于禁止外部改写和改名，不执行删除或正文读取。
            using var handle=CloudFilesNative.CreateFile(path,0x10180,1,0,3,0x02200000,0);
            if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            var guarded=LocalFileState.Read(handle);if(guarded.Identity!=before.Identity||guarded.IsDirectory)throw new IOException("排除标记文件身份已变化。");
            before=guarded;info=CloudFilesNative.Inspect(handle);pin=ReadPin(handle,info?.Info.PinState);
            var record=new Mark(relative,before.Identity,prior?.PreviousPin??pin,excluded?"ApplyPending":"RestorePending",before,DateTimeOffset.UtcNow);
            marks[before.Identity]=record;Save();var written=false;
            try
            {
                // 解除排除时保留用户后来修改的固定意图；此前就已排除的文件恢复为EXCLUDED。
                if(excluded?pin!=CloudFilesNative.PinStateExcluded:pin==CloudFilesNative.PinStateExcluded&&record.PreviousPin!=pin)
                {
                    CloudFilesNative.Check(SetPinForTests?.Invoke(handle,excluded?CloudFilesNative.PinStateExcluded:record.PreviousPin)??CloudFilesNative.SetPinIntent(handle,excluded?CloudFilesNative.PinStateExcluded:record.PreviousPin,0,0));
                    written=true;
                }
                if(excluded&&(ReadPin(handle,CloudFilesNative.Inspect(handle)?.Info.PinState)!=CloudFilesNative.PinStateExcluded||info is null&&CloudShellState.Read(path).ProviderState!=9))
                    throw new IOException("系统排除状态无法可靠读回；上传仍保持排除。");
                if(excluded&&!_definitionReady)
                {
                    CloudRootRegistration.EnsureExclusionDefinitionAsync(definition,token).ConfigureAwait(false).GetAwaiter().GetResult();_definitionReady=true;
                }
                // WinRT只修改本地元数据；后台同步桥接维持属性保护，系统写入开始后等实际完成才释放句柄。
                written|=CloudItemStateProperty.SetAsync(definition,path,false,CloudItemStateProperty.ExclusionId,text,icon,token).ConfigureAwait(false).GetAwaiter().GetResult();
                if(written&&!WriteClose(handle,0x000900ef,0,0,new byte[8],8,out _,0))throw new Win32Exception(Marshal.GetLastWin32Error());
                var after=LocalFileState.Read(handle);var current=CloudFilesNative.Inspect(handle);
                if(after.Identity!=before.Identity||MountPathGuard.FileIdentity(path)!=after.Identity||after.Length!=before.Length||after.LastWriteTime!=before.LastWriteTime||
                    info?.Info.InSyncState!=current?.Info.InSyncState||info?.Info.OnDiskSize!=current?.Info.OnDiskSize||
                    (info is null?current is not null:current is null||!info.Value.Identity.SequenceEqual(current.Value.Identity)))
                    throw new IOException("标记后的文件内容、身份或同步状态不一致，保留恢复记录。");
                if(excluded)marks[before.Identity]=record with{Phase="Applied"};else marks.Remove(before.Identity);
                Save();return new(prior?.Before??before,after,written);
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or Win32Exception or COMException or OperationCanceledException)
            {
                marks[before.Identity]=record with{Error=$"{error.Message}；HRESULT=0x{error.HResult:X8}"};Save();
                // 写入成功而后续确认失败时，仍吸收已证明无正文变化的自有元数据；错误及恢复阶段继续保留。
                if(written){var after=LocalFileState.Read(handle);if(after.Identity==before.Identity&&MountPathGuard.FileIdentity(path)==after.Identity)return new(before,after,true);}
                throw;
            }
        }
    }
    /// <summary>普通文件没有CF占位信息；直接读属性固定意图，EXCLUDED须再经Shell只读属性确认，不能为读回而转换占位。</summary>
    private static uint ReadPin(SafeFileHandle handle,uint? cloudPin)
    {
        if(cloudPin is {} pin)return pin;
        if(!CloudFilesNative.GetFileInformationByHandleEx(handle,9,out var tag,8))throw new Win32Exception(Marshal.GetLastWin32Error());
        return ((tag.Attributes&0x80000)!=0?1u:0)|((tag.Attributes&0x100000)!=0?2u:0);
    }
    [DllImport("kernel32.dll",EntryPoint="DeviceIoControl",SetLastError=true)]
    private static extern bool WriteClose(SafeFileHandle handle,uint control,nint input,uint inputSize,byte[] output,uint outputSize,out uint returned,nint overlap);
}
