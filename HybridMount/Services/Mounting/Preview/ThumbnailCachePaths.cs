namespace HybridMount.Services.Mounting;

/// <summary>仅缩略图使用当前用户固定物理目录；不改变两种运行模式的配置、凭据和索引路径。</summary>
internal static class ThumbnailCachePaths
{
    internal static string DirectoryPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HybridMount.NET","Mounting","Thumbnails");
    internal static string DatabasePath=>Path.Combine(DirectoryPath,"thumbnails.db");
}
