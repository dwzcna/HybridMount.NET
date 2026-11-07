using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>只读Shell状态适配器；只查元数据属性，不允许慢速内容项，不请求图像、网络或同步状态写入。</summary>
internal static class CloudShellState
{
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey{public Guid Format;public uint Id;}
    [StructLayout(LayoutKind.Explicit,Size=24)] private struct Variant
    {[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public uint Value;}
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]int GetCount(out uint count);
        [PreserveSig]int GetAt(uint index,out PropertyKey key);
        [PreserveSig]int GetValue(in PropertyKey key,out Variant value);
        [PreserveSig]int SetValue(in PropertyKey key,in Variant value);
        [PreserveSig]int Commit();
    }
    /// <summary>调用线程建立COM范围，只取元数据，不允许GPS_OPENSLOWITEM；读取异常返回类型，不记录原始系统消息。</summary>
    public static ShellFileStatus Read(string path)
    {
        var initialized=CoInitializeEx(0,0);IPropertyStore? store=null;
        try
        {
            if(initialized<0&&initialized!=unchecked((int)0x80010106))Marshal.ThrowExceptionForHR(initialized);
            var id=typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path,0,0x40,in id,out store));
            var properties=store??throw new COMException("Shell属性存储为空。");
            uint? Value(string name)
            {
                Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName(name,out var key));
                Marshal.ThrowExceptionForHR(properties.GetValue(in key,out var value));
                try{return value.Type is 19 or 3?value.Value:null;}
                finally{PropVariantClear(ref value);}
            }
            return new(Value("System.StorageProviderState"),Value("System.SyncTransferStatus"),Value("System.FilePlaceholderStatus"));
        }
        catch(Exception error){return new(null,null,null,error.GetType().Name);}
        finally{if(store is not null)Marshal.ReleaseComObject(store);if(initialized>=0)CoUninitialize();}
    }
    /// <summary>读取实际包身份和本用户安装清单；不修改注册，不枚举其他账号，不依赖输出目录名称推断模式。</summary>
    public static string EnvironmentDescription()
    {
        string identity;
        try{identity=Windows.ApplicationModel.Package.Current.Id.FullName;}
        catch(InvalidOperationException){identity="无包身份（非打包运行）";}
        var lines=new List<string>{"运行版本："+typeof(MountManager).Assembly.GetName().Version,"运行包身份："+identity};
        try
        {
            var user=WindowsIdentity.GetCurrent().User!.Value;
            var packages=new Windows.Management.Deployment.PackageManager().FindPackagesForUser(user).Where(p=>p.Id.Name=="dwzcn.HybridMount.NET").ToArray();
            if(packages.Length==0)lines.Add("本用户未安装HybridMount MSIX；基础Shell注册不可用。");
            foreach(var package in packages)
            {
                using var reader=XmlReader.Create(Path.Combine(package.InstalledLocation.Path,"AppxManifest.xml"),new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});
                var manifest=XDocument.Load(reader);var cloud=manifest.Descendants().FirstOrDefault(e=>e.Name.LocalName=="CloudFiles");
                lines.Add("已安装包："+package.Id.FullName);
                lines.Add("CloudFiles处理器："+(cloud is null?"未注册":string.Join("、",cloud.Elements().Select(e=>e.Name.LocalName))));
                var thumbnail=cloud?.Elements().FirstOrDefault(e=>e.Name.LocalName=="ThumbnailProviderHandler")?.Attribute("Clsid")?.Value;
                lines.Add("自定义云缩略图入口："+(string.Equals(thumbnail,"A65802EB-4D1C-4C7E-A519-34C76B9BB281",StringComparison.OrdinalIgnoreCase)?"已注册（实验性）":"未启用；挂载开关不能替代安装注册。"));
            }
        }
        catch(Exception error){lines.Add("安装注册查询失败："+error.GetType().Name);}
        return string.Join(Environment.NewLine,lines);
    }
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint mode);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("ole32.dll")]private static extern int PropVariantClear(ref Variant value);
    [DllImport("propsys.dll",CharSet=CharSet.Unicode)]private static extern int PSGetPropertyKeyFromName(string name,out PropertyKey key);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHGetPropertyStoreFromParsingName(string path,nint binding,uint flags,in Guid id,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore? store);
}
