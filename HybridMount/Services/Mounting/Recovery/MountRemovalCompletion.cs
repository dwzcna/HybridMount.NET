using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>独立JSON恢复记录；Schema1兼容旧遗留日志，Schema2增加内容证据和真实视图结果，不改变配置或SQLite。</summary>
internal sealed record MountRemovalJournal(int Schema,OrphanedMountCandidate Root,string Stage,RecoveryShellProof? Shell,int Deleted,
    MountRemovalMode Mode=MountRemovalMode.ClearContents,string? ContentStamp=null,string? FailedStage=null,int? HResult=null,string? Error=null,
    DateTimeOffset? UpdatedUtc=null,bool ExternalVerified=false,string[]? Remaining=null)
{
    internal bool ContentCompleted=>Stage is "ContentComplete" or "Unregistering" or "NativeRemoved" or "Completed";
    internal string Description=>Stage switch
    {
        "ContentComplete" or "Unregistering"=>Mode==MountRemovalMode.ClearContents?"内容已清空，等待注销":"文件已保留，等待注销",
        "NativeRemoved"=>"原生已注销，等待清理 Explorer 入口",
        "Completed"=>ExternalVerified?"注册收尾已核验，等待清除应用记录":"旧版已完成记录，等待核验 Explorer 入口",
        _=>Root.ShellOnly?"等待清理 Explorer 遗留入口":Mode==MountRemovalMode.ClearContents?"内容清空未完成，请继续清空并移除":"内容处理未完成，请继续下载并移除"
    };
}
/// <summary>三种移除路径共用的持久化与注销收尾服务。只有内容证据、原生注销和两视图核验一致才完成；启动只读取。</summary>
internal sealed class MountRemovalCompletion(string directory,IRecoveryShellRegistration shell,Action? ensureRunning=null)
{
    internal Action<string>? StageForTests {get;set;}
    private string FileName(Guid id)=>Path.Combine(directory,id.ToString("N")+".json");
    internal MountRemovalJournal? Read(Guid id)=>File.Exists(FileName(id))?Load(FileName(id)):null;
    internal IEnumerable<MountRemovalJournal> Journals()=>Directory.Exists(directory)?Directory.EnumerateFiles(directory,"*.json").Select(Load).ToArray():[];
    internal MountRemovalJournal Begin(OrphanedMountCandidate root,MountRemovalMode mode)
    {
        ensureRunning?.Invoke();var old=Read(root.MountId);
        if(old is not null&&(old.Root.LocalPath!=root.LocalPath||old.Root.RootIdentity!=root.RootIdentity&&!(old.Stage=="Completed"&&root.ShellOnly&&root.RootIdentity.Length==0)||old.Root.SyncRootId!=root.SyncRootId||old.Mode!=mode&&!(old.Stage=="Completed"&&root.ShellOnly)))throw new IOException("移除日志的身份或方式不一致，保留现场。");
        var journal=old??new(2,root,"Confirmed",null,0,mode);
        if(old is {Stage:"Completed"}&&root.ShellOnly&&!old.Root.ShellOnly)
            journal=old with{Schema=2,Root=root,Mode=MountRemovalMode.PreserveFiles,Stage="Confirmed",ContentStamp=null,ExternalVerified=false};
        if(journal.Shell is null||journal.Schema==1||journal.Shell.External is null&&shell is MaintenanceShellRegistration)
        {
            var fresh=shell.Capture(root with{NamespaceId=journal.Shell?.NamespaceId??root.NamespaceId});
            journal=journal with{Schema=2,Shell=Merge(journal.Shell,fresh)};
        }
        if(journal.ContentCompleted&&journal.ContentStamp is null)
        {
            var stamp=ContentStamp(journal.Root,journal.Mode);
            journal=journal with{ContentStamp=stamp,Root=journal.Root with{ContentStamp=stamp}};
        }
        Save(journal);return journal;
    }
    private static RecoveryShellProof? Merge(RecoveryShellProof? old,RecoveryShellProof? fresh)
    {
        if(old is null)return fresh;if(fresh is null)return old;
        return old with{Roots=old.Roots.Concat(fresh.Roots).DistinctBy(x=>(x.Hive,x.Path)).ToArray(),Namespace=old.Namespace.Concat(fresh.Namespace).DistinctBy(x=>(x.Hive,x.Path)).ToArray(),
            NamespaceId=old.NamespaceId??fresh.NamespaceId,Hidden=old.Hidden??fresh.Hidden,External=old.External??fresh.External};
    }
    internal MountRemovalJournal ContentComplete(MountRemovalJournal journal)
    {
        var stamp=ContentStamp(journal.Root,journal.Mode);return Stage(journal with{ContentStamp=stamp,Root=journal.Root with{ContentStamp=stamp}},"ContentComplete");
    }
    internal MountRemovalJournal Stage(MountRemovalJournal journal,string stage)
    {
        journal=journal with{Stage=stage,Error=null,FailedStage=null,HResult=null,UpdatedUtc=DateTimeOffset.UtcNow};Save(journal);StageForTests?.Invoke(stage);return journal;
    }
    internal void Save(MountRemovalJournal journal)
    {
        Directory.CreateDirectory(directory);var file=FileName(journal.Root.MountId);var temp=file+".tmp";var data=JsonSerializer.SerializeToUtf8Bytes(journal);
        if(data.Length>ShellMaintenanceProcess.MaximumBytes)throw new IOException("移除恢复记录过大，停止操作。");
        try{using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){stream.Write(data);stream.Flush(true);}File.Move(temp,file,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal static MountRemovalJournal Load(string file)
    {
        if(new FileInfo(file).Length>ShellMaintenanceProcess.MaximumBytes)throw new IOException("恢复日志过大。");
        var journal=JsonSerializer.Deserialize<MountRemovalJournal>(File.ReadAllBytes(file),new JsonSerializerOptions{MaxDepth=32})??throw new IOException("恢复日志为空。");
        if(journal.Schema is not(1 or 2)||!new[]{"Confirmed","Clearing","ContentComplete","Unregistering","NativeRemoved","Completed"}.Contains(journal.Stage))throw new IOException("恢复日志版本或阶段未知。");return journal;
    }
    internal void Failure(MountRemovalJournal journal,Exception error)
    {
        // 重新读最近持久阶段，避免收尾中已保存NativeRemoved却被调用方旧快照覆盖。
        var current=Read(journal.Root.MountId)??journal;Save(current with{FailedStage=current.Stage,HResult=error is Win32Exception native?unchecked((int)(0x80070000u|(uint)native.NativeErrorCode)):error.HResult,Error=error.Message,UpdatedUtc=DateTimeOffset.UtcNow});
    }
    /// <summary>持有根路径租约执行WinRT优先注销；只有404／不可见才允许原生回退。原生已移除时不连接、不重做内容。</summary>
    internal MountRemovalJournal Finish(MountRemovalJournal journal,CancellationToken token,Action? additionalValidation=null)
    {
        try
        {
            ensureRunning?.Invoke();token.ThrowIfCancellationRequested();additionalValidation?.Invoke();
            if(!journal.ContentCompleted&&!journal.Root.ShellOnly)throw new IOException("内容处理尚未完成，拒绝注销。");
            var root=journal.Root;using var lease=Hold(root);
            void VerifyContent(){ensureRunning?.Invoke();token.ThrowIfCancellationRequested();additionalValidation?.Invoke();VerifyLocal(root);if(journal.ContentStamp is {} stamp&&ContentStamp(root,journal.Mode)!=stamp)throw new IOException("目录出现新内容或发生改变，停止注销收尾。");}
            VerifyContent();var exists=LocalFileMetadata.TryRead(root.LocalPath) is not null;
            var native=exists?CloudFilesNative.RootIdentity(root.LocalPath,true):null;
            if(native is not null)
            {
                if(root.ShellOnly)throw new IOException("目录重新成为同步根，保留入口。");
                if(journal.Stage is "NativeRemoved" or "Completed")throw new IOException("原生注销后路径被重新注册，停止收尾。");
                ValidateNative(root);journal=Stage(journal,"Unregistering");
                VerifyContent();ValidateNative(root);
                var visible=shell.List().Any(x=>x.Id==root.SyncRootId&&!x.ShellOnly);var fallback=!visible;
                VerifyContent();ValidateNative(root);
                try{shell.Unregister(root.SyncRootId);}
                catch(Exception error)when(error.HResult==unchecked((int)0x80070490)){fallback=true;}
                if(CloudFilesNative.RootIdentity(root.LocalPath,true) is not null)
                {
                    if(!fallback)throw new IOException("WinRT注销后原生根仍存在，保留注销阶段。");
                    VerifyContent();ValidateNative(root);NativeRuntimeClient.Get().Invoke("unregisterNativeRoot",NativeRuntimeArguments.Root(new(root.MountId,"",root.LocalPath,RootIdentity:root.RootIdentity)));
                }
            }
            VerifyContent();if(exists&&CloudFilesNative.RootIdentity(root.LocalPath,true) is not null)throw new IOException("原生同步根仍存在。");
            journal=Stage(journal,"NativeRemoved");
            shell.Cleanup(root,journal.Shell,()=>{VerifyContent();if(LocalFileMetadata.TryRead(root.LocalPath) is not null&&CloudFilesNative.RootIdentity(root.LocalPath,true) is not null)throw new IOException("路径已重新注册，停止Shell清理。");});
            VerifyContent();var remaining=shell.Verify(root,journal.Shell);journal=journal with{Remaining=remaining,ExternalVerified=remaining.Length==0};Save(journal);
            if(remaining.Length!=0)throw new IOException("Explorer注册仍有残留："+string.Join("；",remaining));
            journal=Stage(journal,"Completed");CloudFilesNative.NotifyItemUpdated(root.LocalPath);return journal;
        }
        catch(Exception error){Failure(journal,error);throw;}
    }
    internal static void ValidateNative(OrphanedMountCandidate root)
    {
        var marker=MountPathGuard.ReadRoot(root.LocalPath);if(marker?.MountId!=root.MountId||marker.Owner!=WindowsIdentity.GetCurrent().User!.Value)throw new IOException("原生同步根身份或用户不匹配。");
        // 根自身FileId必须与CFAPI根FileId相同，不能注销父根的子目录。
        var facts=CloudRootPolicy.Read(root.LocalPath);if(facts.FileId!=BitConverter.ToInt64(Convert.FromHexString(root.RootIdentity),8))throw new IOException("目录不是原生根本身。");
    }
    internal static IDisposable? Hold(OrphanedMountCandidate root)
    {
        VerifyLocal(root);return root.RootIdentity.Length==0?null:MountContentCleaner.HoldRoot(new(root.MountId,"",root.LocalPath,false,RootIdentity:root.RootIdentity));
    }
    internal static void VerifyLocal(OrphanedMountCandidate root)
    {
        var info=LocalFileMetadata.TryRead(root.LocalPath);
        if(info is null)
        {
            if(root.ShellOnly&&root.RootIdentity.Length==0)
            {
                for(var cursor=Path.GetDirectoryName(root.LocalPath);cursor is not null;cursor=Path.GetDirectoryName(cursor))
                    if(LocalFileMetadata.TryRead(cursor) is {} ancestor&&(ancestor.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("遗留路径祖先含外部重解析点。");
                return;
            }
            throw new IOException("挂载目录已不存在，保留移除证据。");
        }
        if(root.RootIdentity.Length==0||root.RootIdentity!=MountPathGuard.FileIdentity(root.LocalPath))throw new IOException("挂载根身份发生变化。");
        if((info.Value.Attributes&FileAttributes.Directory)==0)throw new IOException("目标已不是目录。");
        for(var cursor=new DirectoryInfo(root.LocalPath);cursor is not null;cursor=cursor.Parent)
            if((cursor.Attributes&FileAttributes.ReparsePoint)!=0&&(!cursor.FullName.Equals(root.LocalPath,StringComparison.OrdinalIgnoreCase)||!CloudFilesNative.IsCloudPath(root.LocalPath)))throw new IOException("根路径包含外部重解析点。");
        if(root.ShellOnly&&CloudFilesNative.RootIdentity(root.LocalPath,true) is not null)throw new IOException("目录仍有原生同步根，不允许只清入口。");
    }
    /// <summary>内容完成证据只读取属性、FileId及时间，不读取正文。清空要求始终为空，保留模式检测新增、替换和编辑。</summary>
    internal static string ContentStamp(OrphanedMountCandidate root,MountRemovalMode mode)
    {
        VerifyLocal(root);if(root.RootIdentity.Length==0)return "missing";
        if(mode==MountRemovalMode.ClearContents){if(Directory.EnumerateFileSystemEntries(root.LocalPath).Any())throw new IOException("目录不再为空，停止收尾。");return "empty";}
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var pending=new Stack<string>();pending.Push(root.LocalPath);var count=0;
        while(pending.TryPop(out var directory))foreach(var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if(++count>1000000)throw new IOException("内容证据条目超过安全上限。");var metadata=LocalFileMetadata.TryRead(path)??throw new IOException("核验期间条目消失。");
            var value=Path.GetRelativePath(root.LocalPath,path)+"\0"+MountPathGuard.FileIdentity(path)+"\0"+metadata.Length+"\0"+(int)metadata.Attributes+"\0"+File.GetLastWriteTimeUtc(path).Ticks+"\n";hash.AppendData(Encoding.UTF8.GetBytes(value));
            if((metadata.Attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))==FileAttributes.Directory)pending.Push(path);
            if(CloudFilesNative.IsCloudPath(path))throw new IOException("内容处理后仍有云占位，停止注销。");
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

}
