namespace HybridMount.Services.Mounting;

/// <summary>远端删除后的本地清理；原生端在同一保护句柄中核验并按句柄删除。</summary>
internal static class CloudRemoteCleanup
{
    public static void Delete(MountDefinition mount,MountEntry entry)
    {
        if(entry.Protected||entry.Deleted)throw new IOException("本地修改或删除意图受到保护。");
        NativeRuntimeClient.Get().Invoke("deleteRemote",NativeRuntimeArguments.Entry(mount,entry));
    }
}
