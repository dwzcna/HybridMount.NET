using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>只读公开的System.StorageProviderCustomStates二进制容器；用系统反序列化器读取向量，不创建可能崩溃的属性描述或图像。</summary>
internal static class CloudCustomStateReader
{
    private static readonly Guid StateFormat=new("fceff153-e839-4cf3-a9e7-ea22832094b8");
    [StructLayout(LayoutKind.Sequential)]private struct PropertyKey{public Guid Format;public uint Id;}
    // x64和ARM64的PROPVARIANT为24字节，向量的数量/指针位于8/16；只接受经检查的类型。
    [StructLayout(LayoutKind.Explicit,Size=24)]private struct Variant
    {[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public uint Count;[FieldOffset(8)]public nint String;[FieldOffset(16)]public nint Data;}
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]int GetCount(out uint count);
        [PreserveSig]int GetAt(uint index,out PropertyKey key);
        [PreserveSig]int GetValue(in PropertyKey key,out Variant value);
        [PreserveSig]int SetValue(in PropertyKey key,in Variant value);
        [PreserveSig]int Commit();
    }
    [ComImport,Guid("e318ad57-0aa0-450f-aca5-6fab7103d917"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistSerializedPropStorage
    {
        [PreserveSig]int SetFlags(uint flags);
        [PreserveSig]int SetPropertyStorage(nint storage,uint count);
        [PreserveSig]int GetPropertyStorage(out nint storage,out uint count);
    }
    /// <summary>调用方已核对本产品根和文件身份；读取不写属性、不水合正文、不打开慢速项，失败保持原数据。</summary>
    internal static ShellCustomState Read(string path)
    {
        var initialized=CoInitializeEx(0,0);IPropertyStore? source=null,memory=null;Variant container=default;
        try
        {
            if(initialized<0&&initialized!=unchecked((int)0x80010106))Marshal.ThrowExceptionForHR(initialized);
            var id=typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path,0,0,in id,out source));
            Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName("System.StorageProviderCustomStates",out var property));
            Marshal.ThrowExceptionForHR(source.GetValue(in property,out container));
            if(container.Type==0)return new([],[],[]);
            if(container.Type!=0x1011||container.Count is 0 or >1048576||container.Data==0)throw new IOException("自定义状态容器类型或长度无效，保留现场。");
            Marshal.ThrowExceptionForHR(PSCreateMemoryPropertyStore(in id,out memory));
            Marshal.ThrowExceptionForHR(((IPersistSerializedPropStorage)memory).SetPropertyStorage(container.Data,container.Count));
            Marshal.ThrowExceptionForHR(memory.GetCount(out var count));if(count>256)throw new IOException("自定义状态字段超过安全限制。");
            var unknown=false;
            for(uint i=0;i<count;i++){Marshal.ThrowExceptionForHR(memory.GetAt(i,out var key));unknown|=key.Format!=StateFormat||(key.Id!=119&&key.Id is (<121 or >123));}
            return new(Strings(memory,121),Strings(memory,122),Strings(memory,123),unknown,Provider(memory));
        }
        finally
        {
            PropVariantClear(ref container);if(memory is not null)Marshal.ReleaseComObject(memory);if(source is not null)Marshal.ReleaseComObject(source);if(initialized>=0)CoUninitialize();
        }
    }
    /// <summary>系统生成的来源同步根ID独立于三组状态向量，用于防止接管其他提供程序的属性。</summary>
    private static string? Provider(IPropertyStore store)
    {
        var key=new PropertyKey{Format=StateFormat,Id=119};Marshal.ThrowExceptionForHR(store.GetValue(in key,out var value));
        try
        {
            if(value.Type==0)return null;
            if(value.Type!=31||value.String==0)throw new IOException("自定义状态来源无效，保留原属性。");
            var text=Marshal.PtrToStringUni(value.String);if(text is null||text.Length>2048)throw new IOException("自定义状态来源长度无效。");return text;
        }
        finally{PropVariantClear(ref value);}
    }
    private static string[] Strings(IPropertyStore store,uint id)
    {
        var key=new PropertyKey{Format=StateFormat,Id=id};Marshal.ThrowExceptionForHR(store.GetValue(in key,out var value));
        try
        {
            if(value.Type==0)return [];
            if(value.Type!=0x101f||value.Count>256||value.Count>0&&value.Data==0)throw new IOException("自定义状态向量无效，保留现场。");
            var result=new string[value.Count];
            for(var i=0;i<result.Length;i++)
            {var text=Marshal.ReadIntPtr(value.Data,i*IntPtr.Size);result[i]=text==0?"":Marshal.PtrToStringUni(text)??"";if(result[i].Length>32768)throw new IOException("自定义状态文字超过安全限制。");}
            return result;
        }
        finally{PropVariantClear(ref value);}
    }
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint mode);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("ole32.dll")]private static extern int PropVariantClear(ref Variant value);
    [DllImport("propsys.dll",CharSet=CharSet.Unicode)]private static extern int PSGetPropertyKeyFromName(string name,out PropertyKey key);
    [DllImport("propsys.dll")]private static extern int PSCreateMemoryPropertyStore(in Guid id,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHGetPropertyStoreFromParsingName(string path,nint binding,uint flags,in Guid id,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
}
