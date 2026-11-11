using System.Xml;
using System.Xml.Linq;
namespace HybridMount.Models.Mounting.Windows;

/// <summary>菜单和缩略图按当前实际包清单分别启用；不从输出目录或另一个安装身份借用注册。</summary>
public static class ShellExtensionPolicy
{
    private static readonly Lazy<(bool Packaged,bool Menus,bool Thumbnails)> Registration=new(Read);
    public static bool CustomThumbnailsEnabled=>Registration.Value.Thumbnails;
    public static bool CustomContextMenusEnabled=>Registration.Value.Menus;
    public static string PauseReason=>!Registration.Value.Packaged?"非打包运行不启用自有Shell扩展；保留Windows原生状态。":
        Registration.Value.Thumbnails?"已注册自有菜单与数据库缩略图；原生状态由Windows显示。":
        Registration.Value.Menus?"动态菜单已注册；缩略图自动绑定未通过，当前包不注册缩略图。":"当前包未注册自有Shell扩展，保留Windows原生状态。";
    /// <summary>仅明确具有包身份时访问Package.Current；无包不调用WinRT探测，不读取其他安装的清单。</summary>
    private static (bool,bool,bool) Read()=>ReadRegistration(global::HybridMount.Services.WinRegisterService.IsPackaged,
        ()=>global::Windows.ApplicationModel.Package.Current.InstalledLocation.Path);
    /// <summary>在首次能力读取线程查询身份和当前包清单；身份及包位置错误原样传播，清单读取失败保留有包状态并关闭自有扩展。</summary>
    /// <param name="isPackaged">原生实际身份查询，未知错误不得以false代替。</param>
    /// <param name="installedDirectory">仅在有包时读取的当前包位置；测试可提供隔离清单。</param>
    /// <returns>是否有包及当前清单允许的菜单／缩略图能力。</returns>
    internal static (bool Packaged,bool Menus,bool Thumbnails) ReadRegistration(Func<bool> isPackaged,Func<string> installedDirectory)
    {
        if(!isPackaged())return(false,false,false);
        var directory=installedDirectory();
        try
        {
            using var reader=XmlReader.Create(Path.Combine(directory,"AppxManifest.xml"),new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});
            var manifest=XDocument.Load(reader);bool Exists(string path)=>File.Exists(Path.Combine(directory,path));
            return(true,CloudShellState.HasMenuBinding(manifest,Exists),CloudShellState.HasThumbnailBinding(manifest,Exists));
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or XmlException or System.Runtime.InteropServices.COMException){return(true,false,false);}
    }
}
