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
    /// <summary>只读检测实际清单绑定；包内优先核对自身清单，非打包核对已安装的本产品包，不改持久偏好。</summary>
    public static ShellThumbnailCapability ThumbnailCapability()
    {
        var packaged=WinRegisterService.IsPackaged();
        if(!ShellExtensionPolicy.CustomThumbnailsEnabled)return new(false,packaged,ShellExtensionPolicy.PauseReason);
        try
        {
            var packages=packaged?new[]{Windows.ApplicationModel.Package.Current}:new Windows.Management.Deployment.PackageManager().FindPackagesForUser(WindowsIdentity.GetCurrent().User!.Value).Where(p=>p.Id.Name=="dwzcn.HybridMount.NET").ToArray();
            var registered=packaged&&packages.Any(package=>
            {
                using var reader=XmlReader.Create(Path.Combine(package.InstalledLocation.Path,"AppxManifest.xml"),new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});
                return HasThumbnailBinding(XDocument.Load(reader),path=>File.Exists(Path.Combine(package.InstalledLocation.Path,path)));
            });
            return ShellThumbnailCapability.From(packaged,registered);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or XmlException){return ShellThumbnailCapability.From(packaged,false,true);}
    }
    /// <summary>同一应用必须同时声明云入口和实际存在的COM类；拒绝越界DLL路径，避免只因存在同名XML元素就展示不可用开关。</summary>
    internal static bool HasThumbnailBinding(XDocument manifest,Func<string,bool> exists)
    {
        XNamespace foundation="http://schemas.microsoft.com/appx/manifest/foundation/windows10",desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10/3",com="http://schemas.microsoft.com/appx/manifest/com/windows10";
        var expected=new Guid("A65802EB-4D1C-4C7E-A519-34C76B9BB281");
        bool Matches(XAttribute? attribute)=>Guid.TryParse(attribute?.Value,out var value)&&value==expected;
        foreach(var application in manifest.Root?.Element(foundation+"Applications")?.Elements(foundation+"Application")??[])
        {
            var extensions=application.Element(foundation+"Extensions");
            if(extensions is null||!extensions.Elements(desktop+"Extension").Where(x=>x.Attribute("Category")?.Value=="windows.cloudFiles").Elements(desktop+"CloudFiles").Elements(desktop+"ThumbnailProviderHandler").Any(x=>Matches(x.Attribute("Clsid"))))continue;
            foreach(var item in extensions.Elements(com+"Extension").Where(x=>x.Attribute("Category")?.Value=="windows.comServer").Descendants(com+"Class").Where(x=>Matches(x.Attribute("Id"))))
            {
                var server=item.Parent;
                if(server?.Name!=com+"ExeServer"||server.Attribute("Arguments")?.Value!="--thumbnail-server")continue;
                var path=server.Attribute("Executable")?.Value;
                if(path!="HybridMount.NET.exe"||application.Attribute("Executable")?.Value!=path)continue;
                if(exists(path)&&exists("HybridMount.Ext.dll"))return true;
            }
        }
        return false;
    }
    /// <summary>根级诊断区分原生策略、包关联与此调用身份的WinRT可见性；不将缺少列误判为未注册。</summary>
    public static string RootDescription(MountDefinition mount)
    {
        var text=new System.Text.StringBuilder();
        try
        {
            var facts=CloudRootPolicy.Read(mount.LocalPath);text.Append("原生水合策略：").AppendLine(facts.Hydration switch{0=>"Partial（微软当前文档不支持）",1=>"Progressive",2=>"Full",3=>"AlwaysFull",_=>facts.Hydration.ToString()});
            var descriptor=Windows.Storage.Provider.StorageProviderSyncRootManager.GetCurrentSyncRoots().FirstOrDefault(x=>x.Id==CloudRootRegistration.Id(mount));
            text.Append("当前身份WinRT根：").AppendLine(descriptor is null?"未枚举到；不等于原生根不存在":$"已枚举；AllowPinning={descriptor.AllowPinning}；描述符策略={descriptor.HydrationPolicy}");
            using var root=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\"+CloudRootRegistration.Id(mount));
            text.Append("根Shell应用：").AppendLine(root?.GetValue("AUMID") as string??"未读取到");
            text.Append("根显式缩略图类：").AppendLine(root?.GetValue("ThumbnailProvider") as string??"无；现代清单绑定另行核对");
            text.Append("缩略图运行能力：").AppendLine(ThumbnailCapability().Reason);
        }
        catch(Exception error){text.Append("根注册只读查询失败：").AppendLine(error.GetType().Name);}
        return text.ToString();
    }
    /// <summary>读取实际包身份和本用户安装清单；不修改注册，不枚举其他账号，不依赖输出目录名称推断模式。</summary>
    public static string EnvironmentDescription()
    {
        string identity;
        try{identity=Windows.ApplicationModel.Package.Current.Id.FullName;}
        catch(InvalidOperationException){identity="无包身份（非打包运行）";}
        var lines=new List<string>{"运行版本："+typeof(MountManager).Assembly.GetName().Version,"运行包身份："+identity,
            "配置存储模式："+(WinRegisterService.IsPackaged()?"Packaged":"Unpackaged"),"自有Shell扩展："+ShellExtensionPolicy.PauseReason};
        try
        {
            var user=WindowsIdentity.GetCurrent().User!.Value;
            var packages=new Windows.Management.Deployment.PackageManager().FindPackagesForUser(user).Where(p=>p.Id.Name=="dwzcn.HybridMount.NET").ToArray();
            if(packages.Length==0)lines.Add("本用户未安装HybridMount MSIX；Windows原生CFAPI状态不依赖自有扩展注册。");
            foreach(var package in packages)
            {
                using var reader=XmlReader.Create(Path.Combine(package.InstalledLocation.Path,"AppxManifest.xml"),new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});
                var manifest=XDocument.Load(reader);var cloud=manifest.Descendants().FirstOrDefault(e=>e.Name.LocalName=="CloudFiles");
                lines.Add("已安装包："+package.Id.FullName);
                lines.Add("CloudFiles声明："+(cloud is null?"无自定义云声明":!cloud.Elements().Any()?"仅提供程序图标；无自定义处理器":string.Join("、",cloud.Elements().Select(e=>e.Name.LocalName))));
                lines.Add("自定义云缩略图入口："+(HasThumbnailBinding(manifest,path=>File.Exists(Path.Combine(package.InstalledLocation.Path,path)))?"已绑定独立缓存宿主":"未启用；挂载开关不能替代安装注册。"));
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
