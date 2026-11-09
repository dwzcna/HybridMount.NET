namespace HybridMount.Services.Mounting;
public sealed partial class CloudMountSession
{
    /// <summary>只完成已有UNPINNED意图；原生端保护身份、内容和同步状态，返回实际释放量。</summary>
    internal long? CompletePendingUnpin(MountEntry entry)
    {
        if(entry.IsDirectory||entry.Deleted||entry.Protected||entry.RemotePresence==RemotePresence.Deleted)return null;
        // 冷文件及其他Pin意图只读返回，不能在周期维护时反复封闭用户的按需读取。
        var probe=Runtime.InspectAsync(RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath),persisted:true).GetAwaiter().GetResult();
        if(probe.Placeholder is not {PinState:2}||probe.Placeholder.OnDiskSize==0&&(probe.State&0x10)!=0||!BeginMetadataUpdate(entry.Id))return null;
        try{var result=Runtime.Invoke("dehydratePending",EntryArgs(entry));return result.GetProperty("changed").GetBoolean()?NativeRuntimeProtocol.Read<long>(result.GetProperty("freed")):null;}
        finally{EndMetadataUpdate(entry.Id);}
    }
}
