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
    private static (bool,bool,bool) Read()
    {
        string directory;
        try{directory=global::Windows.ApplicationModel.Package.Current.InstalledLocation.Path;}
        catch(InvalidOperationException){return(false,false,false);}
        try
        {
            using var reader=XmlReader.Create(Path.Combine(directory,"AppxManifest.xml"),new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});
            var manifest=XDocument.Load(reader);bool Exists(string path)=>File.Exists(Path.Combine(directory,path));
            return(true,CloudShellState.HasMenuBinding(manifest,Exists),CloudShellState.HasThumbnailBinding(manifest,Exists));
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or XmlException or System.Runtime.InteropServices.COMException){return(true,false,false);}
    }
}
