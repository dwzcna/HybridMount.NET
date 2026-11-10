using System.Runtime.InteropServices;
using System.Security.Principal;
using HybridMount.Commons;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.Recovery;

/// <summary>遗留根发现和显式离线清空。应用持有配置锁及生命周期；不创建文件源、不恢复旧上传、不接管正常挂载。</summary>
internal sealed class OrphanedMountRecovery
{
    private MountRemovalCompletion Completion()=>new(_directory,_shell,_ensureRunning){StageForTests=StageForTests};
    private readonly string _directory;
    private readonly Func<IReadOnlyList<MountDefinition>> _managed;
    private readonly IRecoveryShellRegistration _shell;
    private readonly LoggerService _logger;
    private readonly Action? _ensureRunning;
    private readonly string _sid=WindowsIdentity.GetCurrent().User!.Value;
    internal Action<string>? StageForTests {get;set;}
    internal OrphanedMountRecovery(string directory,Func<IReadOnlyList<MountDefinition>> managed,LoggerService logger,IRecoveryShellRegistration? shell=null,Action? ensureRunning=null)
    {_ensureRunning=ensureRunning;_directory=directory;_managed=managed;_logger=logger;_shell=shell??RecoveryShellRegistration.Create();}

    /// <summary>只枚举系统本产品／当前用户线索及未完成日志；候选都重新验证，发现本身不删除任何内容。</summary>
    internal IReadOnlyList<OrphanedMountCandidate> Discover(CancellationToken token)
    {
        var hints=_shell.List().ToList();
        foreach(var journal in Completion().Journals())
        {token.ThrowIfCancellationRequested();if(journal.Stage!="Completed"||_shell.Verify(journal.Root,journal.Shell).Length>0)hints.Add(new(journal.Root.SyncRootId,journal.Root.LocalPath,journal.Shell?.NamespaceId,journal.Root.ShellOnly));}
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
    {var prefix=ApplicationPaths.ProductScope+"!"+sid+"!";id=Guid.Empty;return value.StartsWith(prefix,StringComparison.Ordinal)&&Guid.TryParseExact(value[prefix.Length..],"N",out id)&&id!=Guid.Empty;}
    private static bool Overlaps(string left,string right)=>MountPathGuard.IsUnder(left,right)||MountPathGuard.IsUnder(right,left);
    /// <summary>目录必须就是原生根本身，而非根的子目录；查询仅使用属性权限。未注册路径只允许继续已有的已清空注销日志。</summary>
    internal OrphanedMountCandidate Inspect(string path)
    {
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var metadata=LocalFileMetadata.TryRead(full);
        if(metadata is null)return InspectShellOnly(full);
        full=Normalize(full);var identity=MountPathGuard.FileIdentity(full);
        foreach(var definition in _managed())if(Overlaps(definition.LocalPath,full))throw new IOException("目录仍由现有配置管理，请使用该挂载的移除入口。");
        var marker=MountPathGuard.ReadRoot(full);
        if(marker is not null)
        {
            if(marker.Owner!=_sid||marker.MountId==Guid.Empty)throw new IOException("同步根不属于当前用户。");
            if(_managed().Any(x=>x.Id==marker.MountId))throw new IOException("该挂载ID仍被现有配置管理。");
            var facts=CloudRootPolicy.Read(full);if(facts.FileId!=BitConverter.ToInt64(Convert.FromHexString(identity),8))throw new IOException("所选目录是同步根的子目录，拒绝清理。");
            return new(marker.MountId,full,identity,CloudRootRegistration.Id(new(marker.MountId,"",full,false)),true);
        }
        if(CloudFilesNative.RootIdentity(full,strict:true) is not null)throw new IOException("不是可验证的本产品同步根。");
        if(Directory.Exists(_directory))
            foreach(var file in Directory.EnumerateFiles(_directory,"*.json"))
            {
                var journal=MountRemovalCompletion.Load(file);var root=journal.Root;
                if(journal.ContentCompleted&&journal.Stage!="Completed"&&root.LocalPath.Equals(full,StringComparison.OrdinalIgnoreCase)&&
                    journal.Mode==MountRemovalMode.ClearContents&&Directory.EnumerateFileSystemEntries(full).Any())throw new IOException("内容完成后出现新文件，保留现场，不重复清空。");
                if(journal.Stage is "ContentComplete" or "Unregistering" or "NativeRemoved" or "Completed"&&root.LocalPath.Equals(full,StringComparison.OrdinalIgnoreCase)&&root.RootIdentity==identity&&
                    TryParseId(root.SyncRootId,_sid,out var id)&&id==root.MountId&&!Directory.EnumerateFileSystemEntries(full).Any())
                {
                    if(journal.Stage=="Completed")
                    {
                        if(_shell.Verify(root,journal.Shell).Length==0)continue;
                        return root with{Registered=false,Error=null,ShellOnly=true,NamespaceId=journal.Shell?.NamespaceId,ContentStamp=null};
                    }
                    return root with{Registered=false,Error=null};
                }
            }
        return InspectShellOnly(full);
    }
    /// <summary>无原生根时只能依靠当前用户精确配对注册发现入口；此类操作保留目录和全部文件。</summary>
    private OrphanedMountCandidate InspectShellOnly(string full)
    {
        foreach(var definition in _managed())if(Overlaps(definition.LocalPath,full))throw new IOException("目录仍由现有配置管理，请使用该挂载的移除入口。");
        foreach(var journal in Completion().Journals().Where(x=>x.Root.ShellOnly&&x.Root.LocalPath.Equals(full,StringComparison.OrdinalIgnoreCase)))
        {
            if(journal.Stage=="Completed"&&_shell.Verify(journal.Root,journal.Shell).Length==0)continue;
            MountRemovalCompletion.VerifyLocal(journal.Root);
            if(journal.ContentStamp is {} stamp&&MountRemovalCompletion.ContentStamp(journal.Root,journal.Mode)!=stamp)throw new IOException("内容证据变化，保留遗留入口供重新核对。");
            return journal.Root with{Registered=false,Error=null};
        }
        var hints=_shell.List().Where(x=>x.ShellOnly&&x.Path.Equals(full,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(hints.Length==0)throw new IOException("目录没有可核验的原生根身份或每根Shell入口，拒绝清理。");
        var hint=hints[0];if(hints.Select(x=>x.Id).Distinct().Count()!=1||!TryParseId(hint.Id,_sid,out var id))throw new IOException("Shell入口身份不唯一。");
        var exists=LocalFileMetadata.TryRead(full) is not null;
        if(exists&&CloudFilesNative.RootIdentity(full,true) is not null)throw new IOException("路径仍有原生根，不能只清理入口。");
        var root=new OrphanedMountCandidate(id,full,exists?MountPathGuard.FileIdentity(full):"",hint.Id,false){ShellOnly=true,NamespaceId=hint.NamespaceId};
        if(_managed().Any(x=>x.Id==id))throw new IOException("该入口仍由现有挂载配置管理。");
        MountRemovalCompletion.VerifyLocal(root);_shell.Capture(root);return root;
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
    /// <summary>执行前重验快照；内容清空只在Working阶段执行，注销和Shell收尾统一交给完成服务。</summary>
    internal async Task<OrphanedMountResult> ClearAsync(OrphanedMountCandidate captured,CancellationToken token,Action<string>? progress=null)
    {
        _ensureRunning?.Invoke();token.ThrowIfCancellationRequested();var current=Inspect(captured.LocalPath);
        if(!captured.CanRemove||current.MountId!=captured.MountId||current.RootIdentity!=captured.RootIdentity||current.SyncRootId!=captured.SyncRootId||current.ShellOnly!=captured.ShellOnly)throw new IOException("遗留根身份变化，请重新核对。");
        var completion=Completion();var journal=completion.Begin(current,current.ShellOnly?MountRemovalMode.PreserveFiles:MountRemovalMode.ClearContents);
        var mount=new MountDefinition(current.MountId,"遗留挂载",current.LocalPath,false,RootIdentity:current.RootIdentity);var errors=new List<string>();CloudMountSession? offline=null;
        void Validate(){_ensureRunning?.Invoke();if(_managed().Any(x=>x.Id==mount.Id||Overlaps(x.LocalPath,mount.LocalPath)))throw new IOException("根身份或配置归属发生变化。");}
        try
        {
            if(!journal.ContentCompleted&&!current.ShellOnly)
            {
                if(current.Registered)
                {
                    var repository=new MountRepository(Path.Combine(_directory,"OfflineIndex"),definitions:new EmptyDefinitionStore());
                    offline=new CloudMountSession(mount,null,repository,_logger,blockHydration:true);offline.Connect();await offline.BeginClearAsync().ConfigureAwait(false);
                }
                journal=completion.Stage(journal,"Clearing");
                var failures=MountContentCleaner.Clear(mount,current.Registered,token,_=>{},(path,status,message)=>
                {if(status=="Completed")journal=journal with{Deleted=journal.Deleted+1};else errors.Add(path+"："+message);progress?.Invoke(path+"："+message);Validate();});
                completion.Save(journal);if(failures>0){completion.Failure(journal,new IOException(string.Join("；",errors)));return new(false,journal.Deleted,journal.Stage,errors);}
                journal=completion.ContentComplete(journal);
            }
            else if(current.ShellOnly&&!journal.ContentCompleted)journal=completion.ContentComplete(journal);
            if(offline is not null){await offline.DisposeAsync().ConfigureAwait(false);offline=null;}
            progress?.Invoke(journal.Description);journal=completion.Finish(journal,token,Validate);return new(true,journal.Deleted,journal.Stage,errors);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or COMException)
        {errors.Add(error.Message);completion.Failure(journal,error);var latest=completion.Read(current.MountId)??journal;return new(false,latest.Deleted,latest.Stage,errors);}
        finally{if(offline is not null)await offline.DisposeAsync().ConfigureAwait(false);}
    }
    private sealed class EmptyDefinitionStore:IMountDefinitionStore
    {public IReadOnlyList<MountDefinition> LoadDefinitions()=>[];public void SaveDefinition(MountDefinition definition)=>throw new NotSupportedException();public void DeleteDefinition(Guid id)=>throw new NotSupportedException();public long CacheLimitBytes{get;set;}}
}
