using System.Runtime.InteropServices;

namespace HybridMount.Tests;

/// <summary>测试专用 DLL 的加载与 Shell 接口桥接；构建时限定隔离管道，不替换产品 DLL。</summary>
internal sealed unsafe class NativePreviewInterop : IDisposable
{
    private readonly nint _library;
    private nint _factory,_initialize,_thumbnail;
    internal static string PipeName=>IpcProtocol.PipeName+".test."+Environment.ProcessId;
    internal static string LibraryPath
    {
        get
        {
            var directory=new DirectoryInfo(AppContext.BaseDirectory);
            while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"Directory.Build.props")))directory=directory.Parent;
            Assert.NotNull(directory);
#if DEBUG
            const string configuration="Debug";
#else
            const string configuration="Release";
#endif
            return Path.Combine(directory.FullName,"artifacts","native-tests",RuntimeInformation.ProcessArchitecture==Architecture.Arm64?"ARM64":"x64",configuration,"HybridMount.Ext.dll");
        }
    }
    internal NativePreviewInterop(string path,bool useShellBinding=false,Action<string>? observe=null,bool nativeServer=false)
    {
        var clsid=new Guid("A749B295-9CB5-4F08-98D4-00EB47345E09");var factoryId=new Guid("00000001-0000-0000-C000-000000000046");nint factory=0;
        if(nativeServer){var code=CoGetClassObject(in clsid,4,0,in factoryId,out factory);if(code<0)Marshal.ThrowExceptionForHR(code);_factory=factory;}
        else{_library=NativeLibrary.Load(LibraryPath);var create=(delegate* unmanaged[Stdcall]<Guid*,Guid*,nint*,int>)NativeLibrary.GetExport(_library,"DllGetClassObject");Assert.Equal(0,create(&clsid,&factoryId,&factory));_factory=factory;}
        var initializeId=new Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841");nint initialize=0;
        Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,nint,Guid*,nint*,int>)Table(_factory)[3])(_factory,0,&initializeId,&initialize));_initialize=initialize;
        var itemId=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");nint item;
        if(useShellBinding){Assert.Equal(0,SHCreateItemFromParsingName(path,0,in itemId,out item));observe?.Invoke("SHCreateItemFromParsingName");}
        else item=Marshal.GetComInterfaceForObject<IdentityShellItem,IIdentityShellItem>(new(path));
        try{Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,nint,uint,int>)Table(_initialize)[3])(_initialize,item,0));}finally{Release(item);}observe?.Invoke("IInitializeWithItem");
        var thumbnailId=new Guid("E357FCCD-A995-4576-B01F-234630154E96");nint thumbnail=0;
        Assert.Equal(0,((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Table(_initialize)[0])(_initialize,&thumbnailId,&thumbnail));_thumbnail=thumbnail;
    }
    private static void** Table(nint value)=>*(void***)value;
    private static void Release(nint value){if(value!=0)((delegate* unmanaged[Stdcall]<nint,uint>)Table(value)[2])(value);}
    internal int Thumbnail(uint size,out uint alpha,out nint bitmap)
    {
        uint kind=0;nint image=0;var status=((delegate* unmanaged[Stdcall]<nint,uint,nint*,uint*,int>)Table(_thumbnail)[3])(_thumbnail,size,&image,&kind);alpha=kind;bitmap=image;return status;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Size{public int Width,Height;}
    /// <summary>调用系统真实图像工厂测量自动入口；不指定本产品处理器，不把此调用等同于 GUI 验收。</summary>
    internal static int ShellImage(string path,int size,uint flags,out nint bitmap)
    {
        var iid=new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");var status=SHCreateItemFromParsingName(path,0,in iid,out var factory);
        bitmap=0;if(status<0)return status;
        try{nint result=0;status=((delegate* unmanaged[Stdcall]<nint,Size,uint,nint*,int>)Table(factory)[3])(factory,new Size{Width=size,Height=size},flags,&result);bitmap=result;return status;}
        finally{Release(factory);}
    }
    public void Dispose(){Release(_thumbnail);Release(_initialize);Release(_factory);if(_library!=0){Assert.Equal(0,((delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(_library,"DllCanUnloadNow"))());NativeLibrary.Free(_library);}}
    [DllImport("ole32.dll")]private static extern int CoGetClassObject(in Guid clsid,uint context,nint reserved,in Guid iid,out nint factory);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path,nint context,in Guid iid,out nint item);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")] private static extern int GetObject(nint bitmap,int length,byte[] data);
    internal static int BitmapWidth(nint bitmap){var bytes=new byte[32];Assert.Equal(32,GetObject(bitmap,bytes.Length,bytes));return BitConverter.ToInt32(bytes,4);}
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint bitmap);
}

/// <summary>同一测试宿主的隔离管道逐项使用，防止两个原生服务争用测试端点。</summary>
[CollectionDefinition("Native IPC",DisableParallelization=true)]
public sealed class NativeIpcCollection;

/// <summary>处理器契约的身份对象：只提供已有路径，不模拟 Shell 的其他属性或内容读取行为。</summary>
[ComVisible(true),Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IIdentityShellItem
{
    [PreserveSig]int BindToHandler(nint context,in Guid handler,in Guid iid,out nint result);
    [PreserveSig]int GetParent(out nint parent);
    [PreserveSig]int GetDisplayName(uint kind,out nint value);
    [PreserveSig]int GetAttributes(uint mask,out uint attributes);
    [PreserveSig]int Compare(nint other,uint hint,out int order);
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class IdentityShellItem(string path) : IIdentityShellItem
{
    public int BindToHandler(nint context,in Guid handler,in Guid iid,out nint result){result=0;return unchecked((int)0x80004001);}
    public int GetParent(out nint parent){parent=0;return unchecked((int)0x80004001);}
    public int GetDisplayName(uint kind,out nint value){value=Marshal.StringToCoTaskMemUni(path);return 0;}
    public int GetAttributes(uint mask,out uint attributes){attributes=0;return unchecked((int)0x80004001);}
    public int Compare(nint other,uint hint,out int order){order=0;return unchecked((int)0x80004001);}
}
