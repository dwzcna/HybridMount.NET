using HybridMount.Infrastructure.Settings;
namespace HybridMount.Infrastructure.Thumbnails;

/// <summary>缩略图跟随实际包LocalState；非打包使用本模式数据根，不申请虚拟化排除。</summary>
internal static class ThumbnailCachePaths
{
    internal static string DirectoryPath=>Path.Combine(ApplicationPaths.DataDirectory,"Mounting","Thumbnails");
    internal static string DatabasePath=>Path.Combine(DirectoryPath,"thumbnails.db"); // 仅旧共享库迁移入口。
    /// <summary>文件名只由挂载GUID构成；调用方不得传入任意数据库路径。</summary>
    internal static string ForMount(Guid mount)=>ForMount(DirectoryPath,mount);
    internal static string ForMount(string directory,Guid mount)=>mount==Guid.Empty?throw new ArgumentException("挂载身份为空。"):Path.Combine(Path.GetFullPath(directory),mount.ToString("N")+".db");
}
