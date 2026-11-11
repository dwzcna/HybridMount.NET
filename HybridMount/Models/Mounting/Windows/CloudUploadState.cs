using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Models.Mounting.Windows;

/// <summary>上传后只转换和标记已核对的当前文件；不删除重建、不脱水、不改变固定状态。</summary>
internal static class CloudUploadState
{
    /// <summary>持久冲突提示同时供扫描与显式重试识别；不自动改变已有上传意图的类型。</summary>
    internal const string TypeConflictMessage=UploadRules.TypeConflictMessage;

    /// <summary>兼容既有内部入口；实际类型规则由领域模型统一提供，不访问原生状态。</summary>
    public static void ValidateTypes(LocalUpload intent,LocalFileStamp? local=null,bool? remoteDirectory=null)
        =>UploadRules.ValidateTypes(intent,local,remoteDirectory);

    /// <summary>完整内容证明适用于同一对象，无论是否已InSync；原生保护范围内仍须精确重验状态戳。</summary>
    internal static bool MatchesRecovery(LocalFileStamp stamp,LocalFileStamp? verifiedStamp)
        =>verifiedStamp is not null&&stamp.Identity==verifiedStamp.Identity&&stamp.Key==verifiedStamp.Key;

    /// <summary>保护引用内核验内容并返回提交后稳定戳；并发编辑返回空值，类型冲突拒绝原生转换。</summary>
    public static LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp? verifiedStamp=null)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);ValidateTypes(prepared,LocalFileState.Read(path),entry.IsDirectory);
        var result=NativeRuntimeClient.Get().Invoke("commitUpload",NativeRuntimeArguments.Entry(mount,entry,new{prepared.SnapshotIdentity,prepared.SnapshotStamp,verifiedStamp=verifiedStamp?.Key}));
        if(result.TryGetProperty("typeConflict",out var conflict)&&conflict.GetBoolean())ValidateTypes(prepared,NativeRuntimeArguments.Stamp(result),entry.IsDirectory);
        return result.GetProperty("committed").GetBoolean()?NativeRuntimeArguments.Stamp(result):null;
    }
}

/// <summary>默认 CFAPI 提交适配器；身份、保护句柄和同步标记仍由同一原生实现负责。</summary>
internal sealed class CloudUploadCommitter : ILocalUploadCommitter,IRecoveryUploadCommitter
{
    public LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared)=>CloudUploadState.Commit(mount,entry,prepared);
    public bool NeedsRecovery(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp stamp)
        =>!entry.IsDirectory&&stamp.Identity==prepared.SnapshotIdentity&&(stamp.Key!=prepared.SnapshotStamp||prepared.Generation!=prepared.SnapshotGeneration)&&CloudFilesNative.HasFullContent(RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath),stamp.Length);
    public LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp? verifiedStamp)=>CloudUploadState.Commit(mount,entry,prepared,verifiedStamp);
}
