using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HybridMount.Tests;

/// <summary>真实Shell文件夹只读检查：枚举默认列、原生菜单和ICONONLY图标；不创建窗口、不执行菜单、不读取正文。</summary>
internal static unsafe class NativeShellInspector
{
    [StructLayout(LayoutKind.Sequential)]private struct Key{public Guid Format;public uint Id;}
    [StructLayout(LayoutKind.Explicit,Size=272)]private struct StrRet{[FieldOffset(0)]public uint Type;[FieldOffset(8)]public nint Pointer;}
    [StructLayout(LayoutKind.Sequential)]private struct Details{public int Format,Width;public StrRet Text;}
    internal sealed record Column(uint Index,string? Name,string Header,uint Flags,int MapResult,int StateResult);
    internal sealed record FileFacts(string Name,string ShellStatus,string[] Menu,string? IconSha256,int IconResult);
    internal sealed record FolderFacts(string ParsingName,int BindResult,Column[] Columns,FileFacts[] Files,string? Error=null);
    private static void** Table(nint value)=>*(void***)value;
    internal static FolderFacts Read(string parsingName,string physicalPath,string[] names)
    {
        var initialized=CoInitializeEx(0,2);nint item=0,folder=0;
        try
        {
            if(initialized<0&&initialized!=unchecked((int)0x80010106))Marshal.ThrowExceptionForHR(initialized);
            var shellItem=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");var folderId=new Guid("93F2F68C-1D1B-11D3-A30E-00C04F79ABD1");
            var handler=new Guid("3981E224-F559-11D3-8E3A-00C04F6837D5");
            var result=SHCreateItemFromParsingName(parsingName,0,in shellItem,out item);if(result<0)return new(parsingName,result,[],[]);
            result=((delegate* unmanaged[Stdcall]<nint,nint,Guid*,Guid*,nint*,int>)Table(item)[3])(item,0,&handler,&folderId,&folder);
            if(result<0)return new(parsingName,result,[],[]);
            var columns=new List<Column>();var invalid=0;
            for(uint index=0;index<512;index++)
            {
                Key key=default;uint state=0;var map=((delegate* unmanaged[Stdcall]<nint,uint,Key*,int>)Table(folder)[19])(folder,index,&key);
                if(map<0){if(++invalid>32)break;continue;}invalid=0;
                var flags=((delegate* unmanaged[Stdcall]<nint,uint,uint*,int>)Table(folder)[16])(folder,index,&state);
                nint name=0;string? canonical=null;if(PSGetNameFromPropertyKey(in key,out name)>=0){try{canonical=Marshal.PtrToStringUni(name);}finally{Marshal.FreeCoTaskMem(name);}}
                var detail=default(Details);var head=((delegate* unmanaged[Stdcall]<nint,nint,uint,Details*,int>)Table(folder)[18])(folder,0,index,&detail);
                var text=new StringBuilder(512);if(head>=0)StrRetToBuf(ref detail.Text,0,text,512);
                columns.Add(new(index,canonical,text.ToString(),state,map,flags));
            }
            var files=new List<FileFacts>();
            foreach(var name in names)
            {
                nint child=0;uint eaten=0,attributes=0;var text=Marshal.StringToCoTaskMemUni(name);
                try
                {
                    var parse=((delegate* unmanaged[Stdcall]<nint,nint,nint,nint,uint*,nint*,uint*,int>)Table(folder)[3])(folder,0,0,text,&eaten,&child,&attributes);
                    if(parse<0){files.Add(new(name,"parse:"+parse,[],null,parse));continue;}
                    var statusColumn=columns.FirstOrDefault(x=>x.Name=="System.StorageProviderUIStatus")??columns.FirstOrDefault(x=>x.Header=="状态");
                    var detail=default(Details);var status="";
                    if(statusColumn is not null&&((delegate* unmanaged[Stdcall]<nint,nint,uint,Details*,int>)Table(folder)[18])(folder,child,statusColumn.Index,&detail)>=0)
                    {var buffer=new StringBuilder(512);StrRetToBuf(ref detail.Text,child,buffer,512);status=buffer.ToString();}
                    var menu=Menu(folder,child);var icon=Icon(Path.Combine(physicalPath,name));files.Add(new(name,status,menu,icon.Hash,icon.Result));
                }
                finally{Marshal.FreeCoTaskMem(text);if(child!=0)Marshal.FreeCoTaskMem(child);}
            }
            return new(parsingName,result,columns.ToArray(),files.ToArray());
        }
        catch(Exception error){return new(parsingName,error.HResult,[],[],error.ToString());}
        finally{if(folder!=0)Marshal.Release(folder);if(item!=0)Marshal.Release(item);if(initialized>=0)CoUninitialize();}
    }
    private static string[] Menu(nint folder,nint child)
    {
        var id=new Guid("000214E4-0000-0000-C000-000000000046");nint menu=0;uint reserved=0;
        var result=((delegate* unmanaged[Stdcall]<nint,nint,uint,nint*,Guid*,uint*,nint*,int>)Table(folder)[10])(folder,0,1,&child,&id,&reserved,&menu);
        if(result<0)return ["GetUIObjectOf:"+result];var popup=CreatePopupMenu();
        try
        {
            result=((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,uint,int>)Table(menu)[3])(menu,popup,0,1,0x7fff,0);
            if(result<0)return ["QueryContextMenu:"+result];var values=new List<string>();ReadMenu(popup,values,0);return values.ToArray();
        }
        finally{if(popup!=0)DestroyMenu(popup);Marshal.Release(menu);}
    }
    private static void ReadMenu(nint menu,List<string> values,int depth)
    {
        if(depth>5)return;
        for(var index=0;index<Math.Min(GetMenuItemCount(menu),256);index++)
        {var text=new StringBuilder(512);GetMenuString(menu,(uint)index,text,512,0x400);if(text.Length>0)values.Add(text.ToString());var nested=GetSubMenu(menu,index);if(nested!=0)ReadMenu(nested,values,depth+1);}
    }
    private static (int Result,string? Hash) Icon(string path)
    {
        var result=NativeThumbnailInterop.ShellImage(path,32,4,out var bitmap);if(result<0||bitmap==0)return(result,null);
        try
        {
            var bytes=new byte[32];if(GetObject(bitmap,32,bytes)!=32)return(result,null);var width=BitConverter.ToInt32(bytes,4);var height=Math.Abs(BitConverter.ToInt32(bytes,8));
            if(width is <1 or >256||height is <1 or >256)return(result,null);var pixels=new byte[width*height*4];var info=new BitmapInfo{Size=40,Width=width,Height=-height,Planes=1,Bits=32};var dc=CreateCompatibleDC(0);
            try{if(GetDIBits(dc,bitmap,0,(uint)height,pixels,ref info,0)!=height)return(result,null);}finally{DeleteDC(dc);}
            return(result,Convert.ToHexString(SHA256.HashData(pixels)));
        }
        finally{NativeThumbnailInterop.DeleteObject(bitmap);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public uint Size;public int Width,Height;public ushort Planes,Bits;public uint Compression,ImageSize;public int X,Y;public uint Used,Important;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHCreateItemFromParsingName(string path,nint context,in Guid id,out nint item);
    [DllImport("shlwapi.dll",EntryPoint="StrRetToBufW",CharSet=CharSet.Unicode)]private static extern int StrRetToBuf(ref StrRet value,nint pidl,StringBuilder buffer,uint length);
    [DllImport("propsys.dll")]private static extern int PSGetNameFromPropertyKey(in Key key,out nint name);
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("user32.dll")]private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")]private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")]private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll",EntryPoint="GetMenuStringW",CharSet=CharSet.Unicode)]private static extern int GetMenuString(nint menu,uint index,StringBuilder text,int length,uint flags);
    [DllImport("user32.dll")]private static extern nint GetSubMenu(nint menu,int index);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")]private static extern int GetObject(nint bitmap,int length,byte[] bytes);
    [DllImport("gdi32.dll")]private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]private static extern int GetDIBits(nint dc,nint bitmap,uint start,uint count,byte[] pixels,ref BitmapInfo info,uint usage);
}
