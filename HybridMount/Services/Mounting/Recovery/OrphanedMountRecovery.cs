using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>遗留根发现和显式离线清空。应用持有配置锁及生命周期；不创建文件源、不恢复旧上传、不接管正常挂载。</summary>
internal sealed class OrphanedMountRecovery
{
    internal sealed record Journal(int Schema,OrphanedMountCandidate Root,string Stage,RecoveryShellProof? Shell,int Deleted);
    private readonly string _directory;
    private readonly Func<IReadOnlyList<MountDefinition>> _managed;
    private readonly IRecoveryShellRegistration _shell;
    private readonly LoggerService _logger;
    private readonly Action? _ensureRunning;
    private readonly string _sid=WindowsIdentity.GetCurrent().User!.Value;
    internal Action<string>? StageForTests {get;set;}
    internal OrphanedMountRecovery(string directory,Func<IReadOnlyList<MountDefinition>> managed,LoggerService logger,IRecoveryShellRegistration? shell=null,Action? ensureRunning=null)
    {_ensureRunning=ensureRunning;_directory=directory;_managed=managed;_logger=logger;_shell=shell??new RecoveryShellRegistration();}

    /// <summary>只枚举系统本产品／当前用户线索及未完成日志；候选都重新验证，发现本身不删除任何内容。</summary>
    internal IReadOnlyList<OrphanedMountCandidate> Discover(CancellationToken token)
    {
        var hints=_shell.List().ToList();
        if(Directory.Exists(_directory))
            foreach(var file in Directory.EnumerateFiles(_directory,"*.json"))
            {token.ThrowIfCancellationRequested();var journal=Load(file);if(journal.Stage!="Completed")hints.Add(new(journal.Root.SyncRootId,journal.Root.LocalPath));}
        var result=new List<OrphanedMountCandidate>();
        foreach(var hint in hints.DistinctBy(x=>x.Path,StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();if(!TryParseId(hint.Id,_sid,out var id))continue;
            if(_managed().Any(x=>x.Id==id||Overlaps(x.LocalPath,hint.Path)))continue;
            try{var candidate=Inspect(hint.Path);if(candidate.SyncRootId!=hint.Id)throw new IOException("注册ID与原生根标记不一致。");result.Add(candidate);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or COMException)
            {result.Add(new(id,hint.Path,"",hint.Id,false,error.Message));}
        }
        return result.OrderBy(x=>x.LocalPath,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static bool TryParseId(string value,string sid,out Guid id)
    {var prefix="HybridMount.NET!"+sid+"!";id=Guid.Empty;return value.StartsWith(prefix,StringComparison.Ordinal)&&Guid.TryParseExact(value[prefix.Length..],"N",out id)&&id!=Guid.Empty;}
    private static bool Overlaps(string left,string right)=>MountPathGuard.IsUnder(left,right)||MountPathGuard.IsUnder(right,left);
    /// <summary>目录必须就是原生根本身，而非根的子目录；查询仅使用属性权限。未注册路径只允许继续已有的已清空注销日志。</summary>
    internal OrphanedMountCandidate Inspect(string path)
    {
        var full=Normalize(path);var identity=MountPathGuard.FileIdentity(full);
        foreach(var definition in _managed())if(Overlaps(definition.LocalPath,full))throw new IOException("目录仍由现有配置管理，请使用该挂载的移除入口。");
        var marker=MountPathGuard.ReadRoot(full);
        if(marker is not null)
        {
            if(marker.Owner!=_sid||marker.MountId==Guid.Empty)throw new IOException("同步根不属于当前用户。");
            if(_managed().Any(x=>x.Id==marker.MountId))throw new IOException("该挂载ID仍被现有配置管理。");
            var buffer=Marshal.AllocHGlobal(8);
            try
            {
                CloudFilesNative.Check(CloudFilesNative.CfGetSyncRootInfoByPath(full,0,buffer,8,out var returned));
                if(returned<8||Marshal.ReadInt64(buffer)!=BitConverter.ToInt64(Convert.FromHexString(identity),8))throw new IOException("所选目录是同步根的子目录，拒绝清理。");
            }
            finally{Marshal.FreeHGlobal(buffer);}
            return new(marker.MountId,full,identity,CloudRootRegistration.Id(new(marker.MountId,"",full,false)),true);
        }
        if(CloudFilesNative.RootIdentity(full,strict:true) is not null)throw new IOException("不是可验证的本产品同步根。");
        if(Directory.Exists(_directory))
            foreach(var file in Directory.EnumerateFiles(_directory,"*.json"))
            {
                var journal=Load(file);var root=journal.Root;
                if(journal.Stage is "Unregistering" or "NativeRemoved"&&root.LocalPath.Equals(full,StringComparison.OrdinalIgnoreCase)&&root.RootIdentity==identity&&
                    TryParseId(root.SyncRootId,_sid,out var id)&&id==root.MountId&&!Directory.EnumerateFileSystemEntries(full).Any())
                    return root with{Registered=false,Error=null};
            }
        throw new IOException("目录没有可核验的原生根身份或已清空注销日志，拒绝清理。");
    }
    private static string Normalize(string path)
    {
        if(!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\",StringComparison.Ordinal))throw new IOException("仅支持本地NTFS目录。");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));var volume=Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!);
        if(full.Equals(volume,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(full)||!new DriveInfo(Path.GetPathRoot(full)!).DriveFormat.Equals("NTFS",StringComparison.OrdinalIgnoreCase))throw new IOException("请选择本地NTFS上的非卷根目录。");
        for(var cursor=new DirectoryInfo(full);cursor is not null;cursor=cursor.Parent)
            if((cursor.Attributes&FileAttributes.ReparsePoint)!=0&&(!cursor.FullName.Equals(full,StringComparison.OrdinalIgnoreCase)||!CloudFilesNative.IsCloudPath(full)))throw new IOException("目录路径包含外部重解析点。");
        return full;
    }
    /// <summary>执行前重验快照；按句柄清空，只有完整清空才注销。取消保留已删结果及阶段，不自动重放。</summary>
    internal async Task<OrphanedMountResult> ClearAsync(OrphanedMountCandidate captured,CancellationToken token,Action<string>? progress=null)
    {
        _ensureRunning?.Invoke();token.ThrowIfCancellationRequested();var current=Inspect(captured.LocalPath);
        if(!captured.CanRemove||current.MountId!=captured.MountId||current.RootIdentity!=captured.RootIdentity||current.SyncRootId!=captured.SyncRootId)throw new IOException("遗留根身份变化，请重新核对。");
        Directory.CreateDirectory(_directory);var file=Path.Combine(_directory,current.MountId.ToString("N")+".json");
        var journal=File.Exists(file)?Load(file):new Journal(1,current,"Confirmed",_shell.Capture(current),0);
        if(journal.Root.MountId!=current.MountId||journal.Root.LocalPath!=current.LocalPath||journal.Root.RootIdentity!=current.RootIdentity)throw new IOException("已有恢复日志身份不一致。");
        var mount=new MountDefinition(current.MountId,"遗留挂载",current.LocalPath,false,RootIdentity:current.RootIdentity);var errors=new List<string>();
        Save(file,journal);CloudMountSession? offline=null;IDisposable? rootLease=null;
        void Stage(string value){journal=journal with{Stage=value};Save(file,journal);progress?.Invoke(value);StageForTests?.Invoke(value);}
        try
        {
            if(current.Registered)
            {
                // 独立空索引只为复用已验证的CFAPI离线删除确认。没有定义、凭据、文件源或上传工作器。
                var repository=new MountRepository(Path.Combine(_directory,"OfflineIndex"),definitions:new EmptyDefinitionStore());
                offline=new CloudMountSession(mount,null,repository,_logger,blockHydration:true);offline.Connect();await offline.BeginClearAsync().ConfigureAwait(false);
                Stage("Clearing");
                var failures=MountContentCleaner.Clear(mount,true,token,_=>{},(path,status,message)=>
                {if(status=="Completed")journal=journal with{Deleted=journal.Deleted+1};else errors.Add(path+"："+message);progress?.Invoke(path+"："+message);_ensureRunning?.Invoke();});
                Save(file,journal);if(failures>0)return new(false,journal.Deleted,journal.Stage,errors);
                if(!MountContentCleaner.IsEmpty(mount))throw new IOException("目录出现新内容，保留现场。");
                token.ThrowIfCancellationRequested();Stage("Unregistering");
                await offline.DisposeAsync().ConfigureAwait(false);offline=null;
                token.ThrowIfCancellationRequested();rootLease=MountContentCleaner.HoldEmptyRoot(mount);RevalidateEmpty(mount,true);
                try{_shell.Unregister(current.SyncRootId);}
                catch(Exception error)when(error is COMException or IOException or UnauthorizedAccessException)
                {_logger.Queue("遗留根","Warning","WinRT注销未完成，将核对原生注册："+error.GetType().Name);}
                // WinRT包归属丢失时只能在同一空根上使用受支持的原生注销；不重建同步根。
                if(CloudFilesNative.RootIdentity(mount.LocalPath,strict:true) is not null)
                {RevalidateEmpty(mount,true);CloudFilesNative.Check(UnregisterNative(mount.LocalPath));}
            }
            rootLease??=MountContentCleaner.HoldEmptyRoot(mount);RevalidateEmpty(mount,false);Stage("NativeRemoved");token.ThrowIfCancellationRequested();
            _shell.Cleanup(current,journal.Shell,()=>RevalidateEmpty(mount,false));
            RevalidateEmpty(mount,false);Stage("Completed");CloudFilesNative.NotifyItemUpdated(mount.LocalPath);
            return new(true,journal.Deleted,journal.Stage,errors);
        }
        catch(OperationCanceledException)
        {Save(file,journal);throw;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or COMException)
        {errors.Add(error.Message);Save(file,journal);return new(false,journal.Deleted,journal.Stage,errors);}
        finally{try{if(offline is not null)await offline.DisposeAsync().ConfigureAwait(false);}finally{rootLease?.Dispose();}}
    }
    private void RevalidateEmpty(MountDefinition mount,bool registered)
    {
        _ensureRunning?.Invoke();var full=Normalize(mount.LocalPath);if(full!=mount.LocalPath||mount.RootIdentity!=MountPathGuard.FileIdentity(full)||_managed().Any(x=>x.Id==mount.Id||Overlaps(x.LocalPath,full)))throw new IOException("根身份或配置归属发生变化。");
        if(!MountContentCleaner.IsEmpty(mount))throw new IOException("目录不再为空，停止注销收尾。");
        var bytes=CloudFilesNative.RootIdentity(full,strict:true);
        if(registered){if(bytes is null||MountPathGuard.ReadRoot(full) is not {} marker||marker.MountId!=mount.Id||marker.Owner!=_sid)throw new IOException("原生同步根归属发生变化。");}
        else if(bytes is not null)throw new IOException("目录重新注册为同步根，停止收尾。");
    }
    private static Journal Load(string path)
    {if(new FileInfo(path).Length>1024*1024)throw new IOException("恢复日志过大。");var value=JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))??throw new IOException("恢复日志为空。");if(value.Schema!=1)throw new IOException("恢复日志版本未知。");return value;}
    private static void Save(string path,Journal journal)
    {
        var temp=path+".tmp";try{using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,journal);file.Flush(true);}File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private sealed class EmptyDefinitionStore:IMountDefinitionStore
    {public IReadOnlyList<MountDefinition> LoadDefinitions()=>[];public void SaveDefinition(MountDefinition definition)=>throw new NotSupportedException();public void DeleteDefinition(Guid id)=>throw new NotSupportedException();public long CacheLimitBytes{get;set;}}
    [DllImport("CldApi.dll",EntryPoint="CfUnregisterSyncRoot",CharSet=CharSet.Unicode)]private static extern int UnregisterNative(string path);
}
