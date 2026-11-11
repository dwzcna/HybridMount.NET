using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Microsoft.Build.Framework;

/// <summary>在MSBuild调用线程按Shell配置生成清单副本；不修改源清单或系统注册，异常记录构建错误。</summary>
public sealed class HybridMountPrepareManifest : Microsoft.Build.Utilities.Task
{
    /// <summary>源清单绝对路径。</summary>
    [Required] public string Source { get; set; }
    /// <summary>输出副本路径，允许相对于ProjectDirectory。</summary>
    [Required] public string Destination { get; set; }
    /// <summary>用于解析相对输出路径的主工程目录。</summary>
    [Required] public string ProjectDirectory { get; set; }
    /// <summary>Native、Menus或MenusAndThumbnails。</summary>
    [Required] public string ShellProfile { get; set; }
    /// <summary>保留原三档注册、启动及维护入口；只在内容改变时写入构建副本，失败返回false。</summary>
    public override bool Execute()
    {
        try
        {
            if (ShellProfile != "Native" && ShellProfile != "Menus" && ShellProfile != "MenusAndThumbnails") throw new InvalidDataException("Shell配置无效。");
            var source = Path.GetFullPath(Source); var destination = Path.GetFullPath(Path.IsPathRooted(Destination) ? Destination : Path.Combine(ProjectDirectory, Destination));
            if (String.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不得覆盖源清单。");
            var manifest = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) manifest.Load(reader);
            var extensions = manifest.SelectSingleNode("//*[local-name()='Application' and @Id='App']/*[local-name()='Extensions']");
            if (extensions == null) throw new InvalidDataException("主应用扩展节点缺失。");
            foreach (var node in extensions.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().ToArray())
                if (node.LocalName == "Extension" && new[] { "windows.cloudFiles", "windows.comServer", "windows.fileExplorerContextMenus" }.Contains(node.GetAttribute("Category"))) extensions.RemoveChild(node);
            const string d3 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/3", d4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4", d5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5", com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
            const string menuId = "59964FC1-D076-4DC1-9778-01F5833A5D4A", thumbId = "A749B295-9CB5-4F08-98D4-00EB47345E09";
            var cloud = Add(Add(extensions, "desktop3", "Extension", d3, "Category", "windows.cloudFiles"), "desktop3", "CloudFiles", d3, "IconResource", @"Assets\HybridMount.png");
            if (ShellProfile == "MenusAndThumbnails")
            {
                // 保留SDK完整子组；未实现处理器只声明GUID_NULL，不为其登记COM类。
                foreach (var name in new[] { "CustomStateHandler", "ThumbnailProviderHandler", "ExtendedPropertyHandler", "BannersHandler" }) Add(cloud, "desktop3", name, d3, "Clsid", name == "ThumbnailProviderHandler" ? thumbId : "00000000-0000-0000-0000-000000000000");
                Add(cloud, "desktop3", "CloudFilesContextMenus", d3);
            }
            if (ShellProfile != "Native")
            {
                var menus = Add(Add(extensions, "desktop4", "Extension", d4, "Category", "windows.fileExplorerContextMenus"), "desktop4", "FileExplorerContextMenus", d4);
                foreach (var type in new[] { "*", "Directory", @"Directory\Background" }) Add(Add(menus, "desktop5", "ItemType", d5, "Type", type), "desktop5", "Verb", d5, "Id", "HybridMount", "Clsid", menuId);
                var server = Add(Add(Add(extensions, "com", "Extension", com, "Category", "windows.comServer"), "com", "ComServer", com), "com", "ExeServer", com, "Executable", "HybridMount.Stock.exe", "Arguments", "--shell-server", "DisplayName", "ms-resource:///Strings/ShellServerDisplayName");
                Add(server, "com", "Class", com, "Id", menuId, "DisplayName", "ms-resource:///Strings/ShellMenuDisplayName");
                if (ShellProfile == "MenusAndThumbnails") Add(server, "com", "Class", com, "Id", thumbId, "DisplayName", "ms-resource:///Strings/ShellThumbnailDisplayName");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)); var text = manifest.OuterXml;
            if (!File.Exists(destination) || File.ReadAllText(destination) != text) File.WriteAllText(destination, text, new UTF8Encoding(false));
            return true;
        }
        catch (Exception error) { Log.LogErrorFromException(error, false); return false; }
    }
    /// <summary>向所属清单追加带命名空间的节点及属性；调用方仅传入本任务固定字段。</summary>
    private static XmlElement Add(XmlNode parent, string prefix, string name, string space, params string[] attributes)
    {
        var node = parent.OwnerDocument.CreateElement(prefix, name, space);
        for (var index = 0; index < attributes.Length; index += 2) node.SetAttribute(attributes[index], attributes[index + 1]);
        parent.AppendChild(node); return node;
    }
}
