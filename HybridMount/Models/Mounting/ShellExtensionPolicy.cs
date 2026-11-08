namespace HybridMount.Models.Mounting;

/// <summary>保留数据库缩略图实现；自动绑定未通过兼容门槛时维持原生显示与菜单。</summary>
public static class ShellExtensionPolicy
{
    public static bool CustomThumbnailsEnabled=>false;
    public static bool CustomContextMenusEnabled=>false;
    public static string PauseReason=>"缩略图自动绑定尚未通过原生菜单兼容验收；本地缓存和宿主实现保留，自有右键菜单扩展继续暂停。";
}
