using System.Globalization;
namespace HybridMount.Models.Mounting.Windows;

/// <summary>本程序状态图标的原生资源引用；Shell按负资源ID取多尺寸ICO，只读资源表，不激活DLL或自有处理器。</summary>
internal static class ShellStatusIconResources
{
    internal const int ExcludedId=102,IssueId=103;
    internal static string Excluded=>Reference(ExcludedId);
    internal static string Issue=>Reference(IssueId);
    /// <summary>始终绑定当前部署目录，升级后预期字符串变化会触发现有安全属性重发；不引用工作目录或第三方资源。</summary>
    internal static string Reference(int id)
    {
        if(id is not(ExcludedId or IssueId))throw new ArgumentOutOfRangeException(nameof(id));
        var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"HybridMount.Ext.dll"));
        if(path.Contains(','))path="\""+path+"\"";
        return path+",-"+id.ToString(CultureInfo.InvariantCulture);
    }
}
