namespace HybridMount.Models.Mounting;

/// <summary>本版本只使用Windows原生云文件显示；自有扩展暂停，偏好和实现保留供后续版本恢复。</summary>
public static class ShellExtensionPolicy
{
    public static bool CustomThumbnailsEnabled=>false;
    public static bool CustomContextMenusEnabled=>false;
    public static string PauseReason=>"本版本已暂停自有缩略图和右键菜单扩展；系统原生云文件状态独立提供。";
}
