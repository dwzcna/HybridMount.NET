using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>只读查询已核验同步根的SDK原生策略；测试编译另提供隔离根策略比较，不改生产根。</summary>
internal static class CloudRootPolicy
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Registration
    {public uint Size;[MarshalAs(UnmanagedType.LPWStr)]public string Name;[MarshalAs(UnmanagedType.LPWStr)]public string Version;public nint Identity;public uint IdentityLength;public nint FileIdentity;public uint FileIdentityLength;public Guid ProviderId;}
    [StructLayout(LayoutKind.Sequential)]private struct Policies{public uint Size,Hydration,Population,InSync,Hardlink,PlaceholderManagement;}
    internal sealed record Facts(ushort Hydration,ushort Modifier,uint Population,uint InSync,uint Hardlink,uint Status,string Provider,string Version,byte[] Identity,long FileId);
    /// <summary>有界读取SDK标准根信息；不打开正文、不查询远端。</summary>
    internal static Facts Read(string path)
    {
        var value=NativeRuntimeClient.Get().RootInfoAsync(path).GetAwaiter().GetResult();CloudFilesNative.Check(value.GetProperty("hresult").GetInt32());
        return Parse(value);
    }
    /// <summary>32位策略位保留有符号旧宿主与无符号新宿主的全部位；超出32位仍拒绝，不将故障状态误当零。</summary>
    internal static Facts Parse(System.Text.Json.JsonElement value)
    {
        static uint Bits(System.Text.Json.JsonElement raw)=>raw.TryGetUInt32(out var bits)?bits:raw.TryGetInt32(out var signed)?unchecked((uint)signed):raw.GetUInt32();
        return new(value.GetProperty("hydration").GetUInt16(),value.GetProperty("modifier").GetUInt16(),Bits(value.GetProperty("population")),Bits(value.GetProperty("inSync")),Bits(value.GetProperty("hardlink")),Bits(value.GetProperty("status")),value.GetProperty("provider").GetString()!,value.GetProperty("version").GetString()!,value.GetProperty("identity").GetBytesFromBase64(),NativeRuntimeProtocol.Read<long>(value.GetProperty("fileId")));
    }
#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>仅测试编译比较三种原生策略；调用方限制在本轮独立根，更新前后核对身份及其他策略。</summary>
    internal static void Set(MountDefinition mount,ushort primary,CancellationToken token)
    {
        if(primary>2)throw new ArgumentOutOfRangeException(nameof(primary));token.ThrowIfCancellationRequested();MountPathGuard.ValidateOperation(mount,mount.LocalPath);
        var before=Read(mount.LocalPath);var identity=Convert.FromHexString(MountPathGuard.FileIdentity(mount.LocalPath));
        if(before.FileId!=BitConverter.ToInt64(identity,8)||!before.Identity.SequenceEqual(MountPathGuard.RootBytes(mount.Id)))throw new IOException("策略更新目标不是已核验的同步根本身。");
        if(before.Hydration==primary)return;var memory=Marshal.AllocHGlobal(before.Identity.Length);
        try
        {
            Marshal.Copy(before.Identity,0,memory,before.Identity.Length);var registration=new Registration{Size=(uint)Marshal.SizeOf<Registration>(),Name=before.Provider,Version=before.Version,Identity=memory,IdentityLength=(uint)before.Identity.Length};
            var policies=new Policies{Size=(uint)Marshal.SizeOf<Policies>(),Hydration=(uint)(primary|(before.Modifier<<16)),Population=before.Population,InSync=before.InSync,Hardlink=before.Hardlink};
            token.ThrowIfCancellationRequested();MountPathGuard.ValidateOperation(mount,mount.LocalPath);CloudFilesNative.Check(Register(mount.LocalPath,registration,policies,1));
            var after=Read(mount.LocalPath);if(mount.RootIdentity!=MountPathGuard.FileIdentity(mount.LocalPath)||after.FileId!=before.FileId||!after.Identity.SequenceEqual(before.Identity)||after.Hydration!=primary||after.Modifier!=before.Modifier||after.Population!=before.Population||after.InSync!=before.InSync||after.Hardlink!=before.Hardlink)throw new IOException("原生策略更新后的身份或策略核验失败。");
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    [DllImport("CldApi.dll",EntryPoint="CfRegisterSyncRoot",CharSet=CharSet.Unicode)]private static extern int Register(string path,in Registration registration,in Policies policies,uint flags);
#endif
}
