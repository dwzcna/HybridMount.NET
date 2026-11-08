namespace HybridMount.Services.Mounting;

/// <summary>仅缩略图使用当前用户固定物理目录；不改变两种运行模式的配置、凭据和索引路径。</summary>
internal static class ThumbnailCachePaths
{
    internal static string DirectoryPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HybridMount.NET","Mounting","Thumbnails");
    internal static string DatabasePath=>Path.Combine(DirectoryPath,"thumbnails.db"); // 仅旧共享库迁移入口。
    /// <summary>文件名只由挂载GUID构成；调用方不得传入任意数据库路径。</summary>
    internal static string ForMount(Guid mount)=>ForMount(DirectoryPath,mount);
    internal static string ForMount(string directory,Guid mount)=>mount==Guid.Empty?throw new ArgumentException("挂载身份为空。"):Path.Combine(Path.GetFullPath(directory),mount.ToString("N")+".db");
}
